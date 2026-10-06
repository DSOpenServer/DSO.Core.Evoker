using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DSO.Core.Evoker.Description;

namespace DSO.Core.Evoker.Commands
{
    /// <summary>
    /// Herhangi bir tipi ya da hazır bir nesneyi JSON komutla kullanılabilir hale getirir (Evoker çekirdeği - plugin değil).
    /// <code>
    /// var t = new EvokerTarget(typeof(InvoiceService));                                // Singleton, parametresiz constructor
    /// var t = new EvokerTarget(typeof(Repo), EvokerLifetime.Scoped, constructorArgs: new object[] { "Server=..." });
    /// var t = EvokerTarget.ForInstance(mevcutNesne);                                     // hazır nesne
    /// var t = new EvokerTarget(typeof(Kur));                                             // static sınıf -> otomatik Static
    /// var r = await t.ExecuteAsync(EvokerCommand.Parse(json));
    /// </code>
    /// Nesne ömrü (<see cref="EvokerLifetime"/>): Singleton (tek nesne, durum korunur), Scoped (komut başına), Transient
    /// (adım başına), Static (nesne yok). Constructor argümanları CLR değerleri (<c>constructorArgs</c>) ya da JSON
    /// (<see cref="WithConstructorJson"/> - kayıtlarda saklanan biçim) olarak verilebilir; constructor seçimi metot
    /// seçimiyle aynı kurallarla yapılır (sayı, optional, tip, isimli argüman).
    /// </summary>
    public sealed class EvokerTarget : IEvokerTarget
    {
        private readonly object?[]? _constructorArgs;
        private JsonElement _constructorJson;
        private readonly bool _fixedInstance;
        private object? _singleton;
        private readonly object _singletonLock = new();

        public Type Type { get; }
        public EvokerLifetime Lifetime { get; }
        public bool IncludeNonPublic { get; }
        /// <summary>Komutta TimeoutMs yoksa kullanılan bekleme üst sınırı (ms). Null = sınırsız.</summary>
        public int? DefaultTimeoutMs { get; set; }

        public string Kind => _fixedInstance ? "Instance" : "Type";
        public string TypeFullName => Type.FullName ?? Type.Name;

        /// <summary>Singleton/hazır nesne hedeflerinde şu anki nesne (henüz oluşturulmadıysa null).</summary>
        public object? Instance => Volatile.Read(ref _singleton);

        public EvokerTarget(Type type, EvokerLifetime lifetime = EvokerLifetime.Singleton, bool includeNonPublic = false, object?[]? constructorArgs = null)
        {
            Type = type ?? throw new ArgumentNullException(nameof(type));
            if (type.ContainsGenericParameters) throw new ArgumentException($"'{type.Name}' açık generic tip - tip argümanları belirtilmeli.", nameof(type));
            // static sınıf: instance oluşturulamaz
            Lifetime = type.IsAbstract && type.IsSealed ? EvokerLifetime.Static : lifetime;
            IncludeNonPublic = includeNonPublic;
            _constructorArgs = constructorArgs;
        }

        private EvokerTarget(object instance, bool includeNonPublic)
        {
            Type = instance.GetType();
            Lifetime = EvokerLifetime.Singleton;
            IncludeNonPublic = includeNonPublic;
            _singleton = instance;
            _fixedInstance = true;
        }

        /// <summary>Hazır bir nesne üzerinde (Singleton gibi davranır, nesne hiç değişmez).</summary>
        public static EvokerTarget ForInstance(object instance, bool includeNonPublic = false) =>
            new(instance ?? throw new ArgumentNullException(nameof(instance)), includeNonPublic);

        /// <summary>Constructor argümanlarını JSON olarak ver (dizi ya da isimli nesne). Kayıtlarda saklanan biçim budur.</summary>
        public EvokerTarget WithConstructorJson(JsonElement constructorArgs)
        {
            if (_fixedInstance) throw new InvalidOperationException("Hazır nesne hedefinde constructor argümanı verilemez.");
            _constructorJson = constructorArgs.ValueKind == JsonValueKind.Undefined ? default : constructorArgs.Clone();
            return this;
        }

        /// <summary>
        /// Bir tipten nesne oluşturur - constructor argümanları JSON (dizi ya da isimli nesne; null/verilmemiş =
        /// parametresiz ya da tüm parametreleri optional constructor). Constructor seçimi metot seçimiyle aynı kural.
        /// Plugin yükleyicisi (in-process ve sandbox worker) bunu kullanır. Hata: EvokerCommandException
        /// (InvalidArguments: uyan constructor yok / değer çevrilemedi; TargetException: constructor'ın kendisi hata verdi).
        /// </summary>
        public static object CreateInstance(Type type, JsonElement? constructorArgs = null, bool includeNonPublic = false)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (type.IsAbstract)
                throw new EvokerCommandException(EvokerErrorCodes.InvalidOperation,
                    $"'{type.Name}' {(type.IsSealed ? "static" : "abstract")} - nesnesi oluşturulamaz.");
            var t = new EvokerTarget(type, EvokerLifetime.Transient, includeNonPublic);
            if (constructorArgs.HasValue) t.WithConstructorJson(constructorArgs.Value);
            return t.CreateInstance(default);
        }

        /// <summary>Singleton nesneyi bırakır - bir sonraki komut yenisini oluşturur (hazır nesne hedefinde etkisiz).</summary>
        public void Reset()
        {
            if (!_fixedInstance) Volatile.Write(ref _singleton, null);
        }

        // ================= Çalıştırma =================

        public Task<EvokerCommandResult> ExecuteAsync(EvokerCommand command, CancellationToken cancellationToken = default) =>
            EvokerCommandRunner.RunAsync(this, command, DefaultTimeoutMs, cancellationToken);

        /// <summary>Bir adım için instance: lifetime'a göre. scope = komut başına tutulan (Scoped).</summary>
        internal object GetInstance(EvokerCommand command, ScopeBox scoped)
        {
            bool overrideArgs = command.ConstructorArgs.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);
            switch (Lifetime)
            {
                case EvokerLifetime.Static:
                    throw new EvokerCommandException(EvokerErrorCodes.InvalidOperation,
                        $"'{Type.Name}' Static hedef - sadece static üyeler çağrılabilir (instance yok).");
                case EvokerLifetime.Singleton:
                    if (overrideArgs)
                        throw new EvokerCommandException(EvokerErrorCodes.InvalidOperation,
                            "\"constructorArgs\" sadece Scoped/Transient hedeflerde komutla verilebilir (Singleton nesne zaten oluşturulmuş/oluşturulacak).");
                    var s = Volatile.Read(ref _singleton);
                    if (s != null) return s;
                    lock (_singletonLock)
                    {
                        s = _singleton ??= CreateInstance(default);
                        return s;
                    }
                case EvokerLifetime.Scoped:
                    return scoped.Value ??= CreateInstance(command.ConstructorArgs);
                default:
                    return CreateInstance(command.ConstructorArgs);
            }
        }

        /// <summary>Yeni bir nesne oluşturur: komuttaki JSON argümanlar &gt; kayıttaki JSON &gt; CLR argümanlar &gt; parametresiz.</summary>
        internal object CreateInstance(JsonElement overrideJson)
        {
            try
            {
                var json = overrideJson.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? _constructorJson : overrideJson;
                if (json.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
                {
                    var ctors = Type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | (IncludeNonPublic ? BindingFlags.NonPublic : 0));
                    var (ctor, args) = EvokerArgumentBinder.Bind(Type.Name + " constructor", ctors, json, null);
                    return EvokerMemberCache.Invoker(ctor)(null, args)!;
                }
                var builder = new EvokerBuilder(Type, IncludeNonPublic);
                if (_constructorArgs != null && _constructorArgs.Length > 0) builder.SetConstructor(_constructorArgs!);
                return builder.CreateInstance();
            }
            catch (EvokerCommandException) { throw; }
            catch (MissingMethodException ex) { throw new EvokerCommandException(EvokerErrorCodes.InvalidArguments, ex.Message, ex); }
            catch (Exception ex)
            {
                var actual = ex is TargetInvocationException { InnerException: { } i } ? i : ex;
                throw new EvokerCommandException(EvokerErrorCodes.TargetException, $"'{Type.Name}' oluşturulurken constructor hata verdi: {actual.Message}", actual)
                { TargetExceptionType = actual.GetType().FullName };
            }
        }

        // ================= Tanım =================

        public async Task<EvokerTypeDescriptor> DescribeAsync(EvokerDescribeOptions? options = null, CancellationToken cancellationToken = default)
        {
            options ??= new EvokerDescribeOptions();
            var d = EvokerDescriber.Describe(Type, new EvokerDescribeOptions
            {
                IncludeNonPublic = options.IncludeNonPublic && IncludeNonPublic, // görünmeyen (çağrılamayan) üyeleri listeleme
                IncludeInherited = options.IncludeInherited,
                IncludeSamples = options.IncludeSamples
            });
            if (options.IncludeValues)
            {
                var instance = Instance; // Singleton oluşturulmadıysa ya da Scoped/Transient ise sadece static değerler
                await EvokerDescriber.CaptureValuesAsync(d, (name, isStatic) =>
                {
                    var m = EvokerMemberCache.DataMember(Type, name, IncludeNonPublic)
                            ?? throw new MissingMemberException(Type.Name, name);
                    if (!isStatic && instance == null)
                        throw new InvalidOperationException(Lifetime is EvokerLifetime.Scoped or EvokerLifetime.Transient
                            ? $"{Lifetime} hedef - kalıcı bir nesne yok, değer okunamaz."
                            : "Nesne henüz oluşturulmadı (ilk komutta oluşturulur).");
                    return Task.FromResult(EvokerMemberCache.Getter(m)(isStatic ? null : instance));
                }, IncludeNonPublic, _fixedInstance ? "Instance" : Lifetime.ToString(), options.MaxValueJsonLength, readStatic: true).ConfigureAwait(false);
            }
            return d;
        }
    }

    /// <summary>Komut başına (Scoped) nesne tutucu.</summary>
    internal sealed class ScopeBox { public object? Value; }

    /// <summary>EvokerTarget üzerinde komut çalıştırma (adımlar, lifetime, timeout, hata eşleme).</summary>
    internal static class EvokerCommandRunner
    {
        private static readonly string[] Ops = { "invoke", "get", "set", "batch" };

        public static async Task<EvokerCommandResult> RunAsync(EvokerTarget target, EvokerCommand command, int? defaultTimeoutMs, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            EvokerCommandResult result;
            try
            {
                Validate(command);
                int? timeout = command.TimeoutMs ?? defaultTimeoutMs;
                var work = RunCoreAsync(target, command, ct);
                if (timeout is > 0 && !work.IsCompleted)
                {
                    using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var winner = await Task.WhenAny(work, Task.Delay(timeout.Value, delayCts.Token)).ConfigureAwait(false);
                    delayCts.Cancel();
                    if (winner != work)
                    {
                        _ = work.ContinueWith(t => _ = t.Exception, TaskScheduler.Default); // gözlemlenmemiş hata kalmasın
                        result = EvokerCommandResult.Fail(EvokerErrorCodes.Timeout,
                            $"Komut {timeout.Value} ms içinde tamamlanmadı (sadece bekleme bırakıldı; kod çalışmaya devam ediyor olabilir).");
                        result.ElapsedMs = sw.Elapsed.TotalMilliseconds;
                        return result;
                    }
                }
                result = await work.ConfigureAwait(false);
            }
            catch (EvokerCommandException ex)
            {
                result = EvokerCommandResult.Fail(ex.Code, ex.Message, ex.TargetExceptionType);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                result = EvokerCommandResult.Fail(EvokerErrorCodes.Cancelled, "Komut iptal edildi.");
            }
            catch (Exception ex)
            {
                result = EvokerCommandResult.Fail(EvokerErrorCodes.InternalError, ex.Message, ex.GetType().FullName);
            }
            result.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            return result;
        }

        private static void Validate(EvokerCommand c)
        {
            if (c.Steps != null)
            {
                if (c.Steps.Count == 0) throw new EvokerCommandException(EvokerErrorCodes.BadRequest, "\"steps\" boş.");
                for (int i = 0; i < c.Steps.Count; i++)
                {
                    if (c.Steps[i] == null) throw new EvokerCommandException(EvokerErrorCodes.BadRequest, $"steps[{i}] boş.");
                    if (c.Steps[i].Steps != null) throw new EvokerCommandException(EvokerErrorCodes.BadRequest, $"steps[{i}]: iç içe \"steps\" desteklenmez.");
                    ValidateStep(c.Steps[i], i);
                }
            }
            else ValidateStep(c, null);
        }

        private static void ValidateStep(EvokerCommand s, int? index)
        {
            string where = index.HasValue ? $"steps[{index}]: " : "";
            var op = (s.Op ?? "invoke").Trim().ToLowerInvariant();
            if (!Ops.Contains(op)) throw new EvokerCommandException(EvokerErrorCodes.BadRequest, $"{where}bilinmeyen op '{s.Op}' (invoke, get, set, batch).");
            if (string.IsNullOrWhiteSpace(s.Member)) throw new EvokerCommandException(EvokerErrorCodes.BadRequest, $"{where}\"member\" gerekli.");
            if (op == "set" && s.Value.ValueKind == JsonValueKind.Undefined) throw new EvokerCommandException(EvokerErrorCodes.BadRequest, $"{where}\"set\" için \"value\" gerekli (null da olabilir).");
            if (op == "batch" && s.ArgsList.ValueKind != JsonValueKind.Array) throw new EvokerCommandException(EvokerErrorCodes.BadRequest, $"{where}\"batch\" için \"argsList\" dizisi gerekli.");
        }

        private static async Task<EvokerCommandResult> RunCoreAsync(EvokerTarget target, EvokerCommand command, CancellationToken ct)
        {
            var scoped = new ScopeBox();
            if (command.Steps == null)
            {
                var step = await RunStepAsync(target, command, command, 0, ct, scoped).ConfigureAwait(false);
                return new EvokerCommandResult
                {
                    Success = step.Success,
                    Result = step.Result,
                    Error = step.Error == null ? null : new EvokerError
                    {
                        Code = step.Error.Code,
                        Message = step.Error.Message,
                        ExceptionType = step.Error.ExceptionType,
                        BatchIndex = step.Error.BatchIndex,
                        Member = step.Error.Member
                    }
                };
            }

            bool stopOnError = command.StopOnError ?? true;
            var steps = new List<EvokerStepResult>();
            EvokerError? firstError = null;
            for (int i = 0; i < command.Steps.Count; i++)
            {
                var s = command.Steps[i];
                if (firstError != null && stopOnError)
                {
                    steps.Add(new EvokerStepResult { Index = i, Op = Op(s), Member = s.Member, As = s.As, Success = false, Skipped = true });
                    continue;
                }
                ct.ThrowIfCancellationRequested();
                var r = await RunStepAsync(target, s, command, i, ct, scoped).ConfigureAwait(false);
                if (r.Error != null) { r.Error.StepIndex = i; firstError ??= r.Error; }
                steps.Add(r);
            }
            return new EvokerCommandResult { Success = firstError == null, Steps = steps, Error = firstError };
        }

        private static string Op(EvokerCommand s) => (s.Op ?? "invoke").Trim().ToLowerInvariant();

        private static async Task<EvokerStepResult> RunStepAsync(EvokerTarget target, EvokerCommand step, EvokerCommand root, int index, CancellationToken ct, ScopeBox scope)
        {
            var sw = Stopwatch.StartNew();
            var r = new EvokerStepResult { Index = index, Op = Op(step), Member = step.Member, As = step.As };
            try
            {
                r.Result = r.Op switch
                {
                    "get" => Get(target, step, root, scope),
                    "set" => Set(target, step, root, scope),
                    "batch" => await BatchAsync(target, step, root, scope, ct).ConfigureAwait(false),
                    _ => await InvokeAsync(target, step.Member!, step.Args, step.ArgTypes, root, scope).ConfigureAwait(false)
                };
                r.Success = true;
            }
            catch (EvokerCommandException ex)
            {
                r.Error = new EvokerError { Code = ex.Code, Message = ex.Message, ExceptionType = ex.TargetExceptionType, BatchIndex = ex.BatchIndex, Member = step.Member };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                var actual = Unwrap(ex);
                r.Error = new EvokerError
                {
                    Code = EvokerErrorCodes.TargetException,
                    Message = actual.Message,
                    ExceptionType = actual.GetType().FullName,
                    Member = step.Member
                };
            }
            r.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            return r;
        }

        private static Exception Unwrap(Exception ex) =>
            ex is TargetInvocationException { InnerException: { } i } ? Unwrap(i)
            : ex is AggregateException { InnerExceptions.Count: 1 } ag ? Unwrap(ag.InnerExceptions[0])
            : ex;

        private static object? InstanceFor(EvokerTarget target, bool isStatic, EvokerCommand root, ScopeBox scope) =>
            isStatic ? null : target.GetInstance(root, scope);

        private static async Task<object?> InvokeAsync(EvokerTarget target, string member, JsonElement args, string[]? argTypes, EvokerCommand root, ScopeBox scope)
        {
            var methods = EvokerMemberCache.Methods(target.Type, member, target.IncludeNonPublic);
            if (methods.Length == 0) throw NotFound(target, member, "metot");
            var (method, values) = EvokerArgumentBinder.Bind(member, methods, args, argTypes);
            var instance = InstanceFor(target, method.IsStatic, root, scope);
            var raw = EvokerMemberCache.Invoker(method)(instance, values);
            return await EvokerMemberCache.AwaitResultAsync(raw, method.ReturnType).ConfigureAwait(false);
        }

        private static async Task<object?> BatchAsync(EvokerTarget target, EvokerCommand step, EvokerCommand root, ScopeBox scope, CancellationToken ct)
        {
            var results = new List<object?>();
            int i = 0;
            foreach (var args in step.ArgsList.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                try { results.Add(await InvokeAsync(target, step.Member!, args, step.ArgTypes, root, scope).ConfigureAwait(false)); }
                catch (EvokerCommandException ex) { throw new EvokerCommandException(ex.Code, $"argsList[{i}]: {ex.Message}", ex) { BatchIndex = i, TargetExceptionType = ex.TargetExceptionType }; }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var actual = Unwrap(ex);
                    throw new EvokerCommandException(EvokerErrorCodes.TargetException, $"argsList[{i}]: {actual.Message}", actual)
                    { BatchIndex = i, TargetExceptionType = actual.GetType().FullName };
                }
                i++;
            }
            return results;
        }

        private static object? Get(EvokerTarget target, EvokerCommand step, EvokerCommand root, ScopeBox scope)
        {
            var m = EvokerMemberCache.DataMember(target.Type, step.Member!, target.IncludeNonPublic) ?? throw NotFound(target, step.Member!, "property/field");
            if (m is PropertyInfo p && p.GetGetMethod(target.IncludeNonPublic) == null)
                throw new EvokerCommandException(EvokerErrorCodes.InvalidOperation, $"'{p.Name}' property'sinin (erişilebilir) get'i yok.");
            return EvokerMemberCache.Getter(m)(InstanceFor(target, EvokerMemberCache.IsStatic(m), root, scope));
        }

        private static object? Set(EvokerTarget target, EvokerCommand step, EvokerCommand root, ScopeBox scope)
        {
            var m = EvokerMemberCache.DataMember(target.Type, step.Member!, target.IncludeNonPublic) ?? throw NotFound(target, step.Member!, "property/field");
            if (m is PropertyInfo p && p.GetSetMethod(target.IncludeNonPublic) == null)
                throw new EvokerCommandException(EvokerErrorCodes.InvalidOperation, $"'{p.Name}' property'sinin (erişilebilir) set'i yok.");
            var type = EvokerMemberCache.DataType(m);
            object? value;
            try { value = Conversion.EvokerValueConverter.FromJson(step.Value, type); }
            catch (Exception ex)
            {
                throw new EvokerCommandException(EvokerErrorCodes.InvalidArguments,
                    $"'{m.Name}' ({EvokerDescriber.Friendly(type)}) için değer çevrilemedi: {ex.Message}", ex);
            }
            var setter = EvokerMemberCache.Setter(m);
            setter(InstanceFor(target, EvokerMemberCache.IsStatic(m), root, scope), value);
            return null;
        }

        private static EvokerCommandException NotFound(EvokerTarget target, string member, string what)
        {
            var names = target.Type.GetMethods(EvokerMemberCache.Flags(target.IncludeNonPublic))
                .Where(m => !m.IsSpecialName && m.DeclaringType != typeof(object) && !m.Name.StartsWith("<")).Select(m => m.Name)
                .Concat(target.Type.GetProperties(EvokerMemberCache.Flags(target.IncludeNonPublic)).Select(p => p.Name))
                .Distinct().OrderBy(n => n).Take(40);
            return new EvokerCommandException(EvokerErrorCodes.MemberNotFound,
                $"'{target.Type.Name}' üzerinde '{member}' adlı {what} bulunamadı{(target.IncludeNonPublic ? "" : " (sadece public üyeler görünür)")}. " +
                $"Mevcut üyeler: {string.Join(", ", names)}");
        }
    }
}