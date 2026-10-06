using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DSO.Core.Evoker.Description;

namespace DSO.Core.Evoker.Commands
{
    /// <summary>
    /// Komut çalıştırılabilen hedeflerin Guid anahtarlı listesi - "sistemdeki kütüphaneleri Evoker aracılığıyla kullan"
    /// katmanı. İki erişim seviyesi:
    ///   1) KAYITLI hedefler: kod ne kaydederse (tip, hazır nesne, plugin). Anahtar her zaman Guid; Name sadece açıklama.
    ///   2) İSİMLE açılan tipler: sadece <see cref="AllowTypesFrom"/> ile izin verilen assembly/namespace'lerdeki tipler
    ///      (varsayılan: hiçbiri). Web'e açılan API'de "herhangi bir tipi isimle çağır" bu listeyle sınırlanır.
    /// <code>
    /// var catalog = new EvokerCatalog();
    /// Guid fatura = catalog.Register(typeof(InvoiceService), name: "Fatura servisi");                 // Singleton
    /// Guid kur    = catalog.Register(typeof(Kur));                                                    // static sınıf
    /// Guid cache  = catalog.RegisterInstance(appCache, name: "Uygulama cache'i");
    /// var r = await catalog.ExecuteAsync(fatura, """{ "op":"invoke", "member":"Kes", "args":{ "musteriKodu":"C001" } }""");
    /// catalog.AllowTypesFrom("Acme.*");
    /// var r2 = await catalog.ExecuteOnTypeAsync("Acme.Finance.Kur", EvokerCommand.Invoke("Bugun"));
    /// </code>
    /// </summary>
    public sealed class EvokerCatalog
    {
        private readonly ConcurrentDictionary<Guid, EvokerCatalogEntry> _entries = new();
        private readonly List<string> _allowed = new();
        private readonly object _allowLock = new();

        // ================= Kayıt =================

        /// <summary>Herhangi bir hedefi kaydeder (ör. PluginTarget). key verilmezse yeni Guid.</summary>
        public Guid Register(IEvokerTarget target, string? name = null, Guid? key = null)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            var k = key ?? Guid.NewGuid();
            var entry = new EvokerCatalogEntry(k, string.IsNullOrWhiteSpace(name) ? null : name!.Trim(), target, DateTime.UtcNow);
            if (!_entries.TryAdd(k, entry)) throw new InvalidOperationException($"'{k}' anahtarı zaten kayıtlı.");
            return k;
        }

        /// <summary>Bir tipi kaydeder. Nesne ömrü <paramref name="lifetime"/> (static sınıflar otomatik Static).</summary>
        public Guid Register(Type type, EvokerLifetime lifetime = EvokerLifetime.Singleton, string? name = null,
            bool includeNonPublic = false, object?[]? constructorArgs = null, Guid? key = null) =>
            Register(new EvokerTarget(type, lifetime, includeNonPublic, constructorArgs), name, key);

        public Guid Register<T>(EvokerLifetime lifetime = EvokerLifetime.Singleton, string? name = null,
            bool includeNonPublic = false, object?[]? constructorArgs = null, Guid? key = null) =>
            Register(typeof(T), lifetime, name, includeNonPublic, constructorArgs, key);

        /// <summary>Bir tipi JSON constructor argümanlarıyla kaydeder (kayıtların saklandığı biçim).</summary>
        public Guid RegisterWithJsonConstructor(Type type, JsonElement constructorArgs, EvokerLifetime lifetime = EvokerLifetime.Singleton,
            string? name = null, bool includeNonPublic = false, Guid? key = null) =>
            Register(new EvokerTarget(type, lifetime, includeNonPublic).WithConstructorJson(constructorArgs), name, key);

        /// <summary>Hazır bir nesneyi kaydeder (nesne hiç değişmez).</summary>
        public Guid RegisterInstance(object instance, string? name = null, bool includeNonPublic = false, Guid? key = null) =>
            Register(EvokerTarget.ForInstance(instance, includeNonPublic), name, key);

        public bool Unregister(Guid key) => _entries.TryRemove(key, out _);

        /// <summary>Kaydın adını değiştirir (sadece açıklama).</summary>
        public bool Rename(Guid key, string? name)
        {
            if (!_entries.TryGetValue(key, out var e)) return false;
            e.Name = string.IsNullOrWhiteSpace(name) ? null : name!.Trim();
            return true;
        }

        public EvokerCatalogEntry? Find(Guid key) => _entries.TryGetValue(key, out var e) ? e : null;

        public IReadOnlyList<EvokerCatalogEntry> Entries =>
            _entries.Values.OrderBy(e => e.RegisteredUtc).ToList();

        // ================= Çalıştırma =================

        public Task<EvokerCommandResult> ExecuteAsync(Guid key, string commandJson, CancellationToken ct = default)
        {
            EvokerCommand cmd;
            try { cmd = EvokerCommand.Parse(commandJson); }
            catch (EvokerCommandException ex) { return Task.FromResult(EvokerCommandResult.Fail(ex.Code, ex.Message)); }
            return ExecuteAsync(key, cmd, ct);
        }

        public Task<EvokerCommandResult> ExecuteAsync(Guid key, EvokerCommand command, CancellationToken ct = default)
        {
            var e = Find(key);
            if (e == null) return Task.FromResult(EvokerCommandResult.Fail(EvokerErrorCodes.TargetNotFound, $"'{key}' anahtarlı kayıt yok."));
            return e.Target.ExecuteAsync(command, ct);
        }

        public Task<EvokerTypeDescriptor> DescribeAsync(Guid key, EvokerDescribeOptions? options = null, CancellationToken ct = default)
        {
            var e = Find(key) ?? throw new KeyNotFoundException($"'{key}' anahtarlı kayıt yok.");
            return e.Target.DescribeAsync(options, ct);
        }

        // ================= İsimle açılan tipler (izin listesi) =================

        /// <summary>
        /// İsimle erişime izin verilen assembly / namespace / tip kalıpları. "Acme.*" = Acme assembly'si ya da Acme
        /// namespace'i ve altındakiler; "Acme.Billing" = birebir assembly adı, namespace (ve altları) ya da tip tam adı;
        /// "*" = her şey (önerilmez - web'e açık bir API'de sunucuda keyfi kod çalıştırmak demektir).
        /// </summary>
        public EvokerCatalog AllowTypesFrom(params string[] patterns)
        {
            lock (_allowLock)
                foreach (var p in patterns.Where(p => !string.IsNullOrWhiteSpace(p)))
                    if (!_allowed.Contains(p.Trim())) _allowed.Add(p.Trim());
            return this;
        }

        public IReadOnlyList<string> AllowedPatterns { get { lock (_allowLock) return _allowed.ToList(); } }

        public bool IsTypeAllowed(Type type)
        {
            if (type == null) return false;
            string asm = type.Assembly.GetName().Name ?? "";
            string ns = type.Namespace ?? "";
            string full = type.FullName ?? type.Name;
            foreach (var p in AllowedPatterns)
            {
                if (p == "*") return true;
                if (p.EndsWith(".*"))
                {
                    var prefix = p[..^2];
                    if (Same(asm, prefix) || asm.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase)
                        || Same(ns, prefix) || ns.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase)) return true;
                }
                else if (Same(asm, p) || Same(ns, p) || ns.StartsWith(p + ".", StringComparison.OrdinalIgnoreCase) || Same(full, p)) return true;
            }
            return false;

            static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>İzin verilen, yüklü assembly'lerdeki public tipler (arama: ad içinde geçen metin).</summary>
        public IReadOnlyList<Type> ListAllowedTypes(string? search = null, int max = 200)
        {
            if (AllowedPatterns.Count == 0) return Array.Empty<Type>();
            return AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic)
                .SelectMany(SafeTypes)
                .Where(t => t.IsPublic && !t.IsInterface && !t.ContainsGenericParameters && !t.IsEnum && !typeof(Delegate).IsAssignableFrom(t) && IsTypeAllowed(t))
                .Where(t => string.IsNullOrWhiteSpace(search) || (t.FullName ?? t.Name).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(t => t.FullName)
                .Take(max)
                .ToList();
        }

        /// <summary>
        /// İsimle bir tipi bulur (EvokerEngine.ResolveType kuralları) ve izin listesini kontrol eder.
        /// İzin yoksa EvokerCommandException(NotAllowed); bulunamazsa TargetNotFound; belirsizse AmbiguousMatch.
        /// </summary>
        public Type ResolveAllowedType(string typeName)
        {
            Type t;
            try { t = EvokerEngine.ResolveType(typeName); }
            catch (AmbiguousMatchException ex) { throw new EvokerCommandException(EvokerErrorCodes.AmbiguousMatch, ex.Message, ex); }
            catch (TypeLoadException ex) { throw new EvokerCommandException(EvokerErrorCodes.TargetNotFound, ex.Message, ex); }
            if (!IsTypeAllowed(t))
                throw new EvokerCommandException(EvokerErrorCodes.NotAllowed,
                    $"'{t.FullName}' isimle erişime açık değil (izin listesi: {(AllowedPatterns.Count == 0 ? "boş" : string.Join(", ", AllowedPatterns))}).");
            return t;
        }

        /// <summary>
        /// İzin verilen bir tipte komut çalıştırır - kayıt gerekmez. Nesne ömrü Scoped (komut başına bir nesne; constructor
        /// argümanları komuttaki "constructorArgs" ile), static üyeler ve static sınıflar için nesne oluşturulmaz.
        /// </summary>
        public async Task<EvokerCommandResult> ExecuteOnTypeAsync(string typeName, EvokerCommand command, bool includeNonPublic = false, CancellationToken ct = default)
        {
            try
            {
                var t = ResolveAllowedType(typeName);
                return await new EvokerTarget(t, EvokerLifetime.Scoped, includeNonPublic).ExecuteAsync(command, ct).ConfigureAwait(false);
            }
            catch (EvokerCommandException ex) { return EvokerCommandResult.Fail(ex.Code, ex.Message); }
        }

        public Task<EvokerTypeDescriptor> DescribeTypeAsync(string typeName, EvokerDescribeOptions? options = null, bool includeNonPublic = false) =>
            new EvokerTarget(ResolveAllowedType(typeName), EvokerLifetime.Scoped, includeNonPublic).DescribeAsync(options);

        private static IEnumerable<Type> SafeTypes(Assembly a)
        {
            try { return a.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null)!; }
            catch { return Array.Empty<Type>(); }
        }
    }

    /// <summary>Katalog kaydı.</summary>
    public sealed class EvokerCatalogEntry
    {
        internal EvokerCatalogEntry(Guid key, string? name, IEvokerTarget target, DateTime registeredUtc)
        {
            Key = key; Name = name; Target = target; RegisteredUtc = registeredUtc;
        }

        public Guid Key { get; }
        /// <summary>Sadece açıklama (boş olabilir, tekrar edebilir).</summary>
        public string? Name { get; internal set; }
        public IEvokerTarget Target { get; }
        public DateTime RegisteredUtc { get; }
        public string Kind => Target.Kind;
        public string TypeFullName => Target.TypeFullName;
        public EvokerLifetime? Lifetime => (Target as EvokerTarget)?.Lifetime;
    }
}