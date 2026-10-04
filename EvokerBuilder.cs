using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace DSO.Core.Evoker
{
    public class EvokerBuilder
    {
        private readonly Type _type;
        private readonly bool _includeNonPublic;
        private object[]? _constructorArgs;
        private object? _existingInstance; // <-- Eklendi

        public EvokerBuilder(Type type, bool includeNonPublic = false)
        {
            _type = type ?? throw new ArgumentNullException(nameof(type));
            _includeNonPublic = includeNonPublic;
        }

        /// <summary>Bu builder'ın bağlı olduğu Type - dışarıdan salt-okunur erişim (bkz. DSO.Core.Evoker.Plugins).</summary>
        public Type Type => _type;

        /// <summary>SADECE SetInstance ile bağlanmış nesneyi döner - null ise sabit instance yok (bkz. EvokerBuilderPropertyExtensions).</summary>
        public object? Instance => _existingInstance;

        /// <summary>
        /// Constructor'da verilen includeNonPublic - dışarıdan salt-okunur erişim. Invoke/Execute zaten
        /// bunu GetMethodInfo'da kullanıyordu (private/protected metotlar); GetValue/SetValue (bkz.
        /// EvokerBuilderPropertyExtensions) de private/protected property ve field'lar için AYNI bayrağı
        /// kullanır - "bu builder private üyeleri görebiliyor mu" tek bir yerde (constructor'da) karar
        /// verilir, Invoke/Execute/GetValue/SetValue hepsi bu tek karara uyar.
        /// </summary>
        public bool IncludeNonPublic => _includeNonPublic;

        // Var olan bir nesne örneğini bağlamak için
        public EvokerBuilder SetInstance(object instance)
        {
            _existingInstance = instance ?? throw new ArgumentNullException(nameof(instance));
            return this;
        }

        public EvokerBuilder SetConstructor(params object[] args)
        {
            _constructorArgs = args;
            return this;
        }

        // --- Doğrudan çağrı (sıcak yol) ---
        // PERFORMANS: Invoke/Execute artık GetFunc'ın döndürdüğü closure'ı ÜRETMİYOR ve cache anahtarı için
        // string ÜRETMİYOR (bkz. ArgTypeKey). Derlenmiş invoker cache'ten (önce bu builder'ın son kullandığı
        // girdi, yoksa global cache) alınıp doğrudan çağrılıyor. Davranış aynı: aynı metot seçimi, aynı
        // instance çözümü (SetInstance / SetConstructor / her çağrıda yeni nesne).
        public object? Invoke(string methodName, params object[] args)
        {
            var cm = GetCachedFunc<object>(methodName, args);
            return ((Func<object?, object[], object>)cm.Invoker)(cm.IsStatic ? null : ResolveInstance(), args);
        }

        public TReturn? Invoke<TReturn>(string methodName, params object[] args)
        {
            var cm = GetCachedFunc<TReturn>(methodName, args);
            return ((Func<object?, object[], TReturn>)cm.Invoker)(cm.IsStatic ? null : ResolveInstance(), args);
        }

        public void Execute(string methodName, params object[] args)
        {
            var cm = GetCachedAction(methodName, args);
            ((Action<object?, object[]>)cm.Invoker)(cm.IsStatic ? null : ResolveInstance(), args);
        }

        public async Task ExecuteAsync(string methodName, params object[] args)
        {
            var task = Invoke<Task>(methodName, args);
            if (task != null) await task;
        }

        public async Task<TReturn?> InvokeAsync<TReturn>(string methodName, params object[] args)
        {
            var task = Invoke<Task<TReturn>>(methodName, args);
            return task != null ? await task : default;
        }

        // --- Delegate Üretimi ve Caching ---
        // ÖNEMLİ: Cache'lenen delegate artık instance/constructor argümanlarını İÇİNDE BARINDIRMIYOR.
        // Instance her çağrıda ResolveInstance() ile ayrı üretilip delegate'e PARAMETRE olarak geçiliyor.
        // Böylece aynı (Type, Method, ReturnType) kombinasyonuna sahip farklı EvokerBuilder
        // örnekleri (farklı SetInstance/SetConstructor ile) birbirinin sonucunu ezmiyor.
        public Func<object[], TReturn> GetFunc<TReturn>(string methodName, object[]? sampleArgs = null)
        {
            var cached = GetCachedFunc<TReturn>(methodName, sampleArgs);
            var typedInvoker = (Func<object?, object[], TReturn>)cached.Invoker;

            if (cached.IsStatic) return args => typedInvoker(null, args);
            var bound = _existingInstance;
            if (bound != null) return args => typedInvoker(bound, args);
            return args => typedInvoker(ResolveInstance(), args);
        }

        public Action<object[]> GetAction(string methodName, object[]? sampleArgs = null)
        {
            var cached = GetCachedAction(methodName, sampleArgs);
            var typedInvoker = (Action<object?, object[]>)cached.Invoker;

            if (cached.IsStatic) return args => typedInvoker(null, args);
            var bound = _existingInstance;
            if (bound != null) return args => typedInvoker(bound, args);
            return args => typedInvoker(ResolveInstance(), args);
        }

        private const string KindFunc = "Func", KindAction = "Action", KindTypedFunc = "TFunc", KindTypedAction = "TAction";

        private CachedMethod GetCachedFunc<TReturn>(string methodName, object[]? sampleArgs)
        {
            var key = new CacheKey(_type, methodName, KindFunc, typeof(TReturn), ArgTypeKey.From(sampleArgs), _includeNonPublic);
            var last = _last;
            if (last != null && last.Key.Equals(key)) return last.Method;
            // static lambda + state: isabet durumunda closure ALLOCATION'ı olmasın.
            var cm = Cache.GetOrAdd(key, static (k, st) => st.Self.BuildCachedFunc<TReturn>(k.Method, st.Args), (Self: this, Args: sampleArgs));
            _last = new LastHit(key, cm);
            return cm;
        }

        private CachedMethod GetCachedAction(string methodName, object[]? sampleArgs)
        {
            var key = new CacheKey(_type, methodName, KindAction, typeof(void), ArgTypeKey.From(sampleArgs), _includeNonPublic);
            var last = _last;
            if (last != null && last.Key.Equals(key)) return last.Method;
            var cm = Cache.GetOrAdd(key, static (k, st) => st.Self.BuildCachedAction(k.Method, st.Args), (Self: this, Args: sampleArgs));
            _last = new LastHit(key, cm);
            return cm;
        }

        // Bu builder'ın EN SON kullandığı cache girdisi. Aynı builder üzerinden aynı metodu tekrar tekrar
        // çağırmak (en yaygın kullanım) global sözlüğe hiç uğramaz. Tek referans atamasıyla değiştirildiği
        // için thread-safe (yarış durumunda en kötü ihtimalle global cache'e düşülür).
        private sealed class LastHit
        {
            public readonly CacheKey Key;
            public readonly CachedMethod Method;
            public LastHit(CacheKey key, CachedMethod method) { Key = key; Method = method; }
        }
        private LastHit? _last;

        // Cache anahtarı: (plugin/hedef Type NESNESİ, metot adı, tür, dönüş Type NESNESİ, argüman tip imzası, NonPublic).
        //
        // ÖNEMLİ DÜZELTME: eskiden anahtar tamamen string'di ve tip kimliği AssemblyQualifiedName ile,
        // dönüş/argüman tipleri de sadece .Name ile tutuluyordu. Bu iki durumda YANLIŞ delegate döndürüyordu:
        //   1) Aynı DLL iki ayrı AssemblyLoadContext'e yüklendiğinde (ya da unload edilip yeni sürümü
        //      yüklendiğinde) iki FARKLI Type aynı AssemblyQualifiedName'e sahip olur -> birinin derlenmiş
        //      delegate'i diğerinin nesnesiyle çağrılıp InvalidCastException ("[A]X cannot be cast to [B]X").
        //   2) Farklı namespace'te aynı isimli tipler (A.Result / B.Result) dönüş ya da argüman tipi olarak
        //      aynı anahtara düşerdi.
        // Type nesneleri referans eşitliğiyle karşılaştırılır - Reflection.Emit'in dinamik tipleri
        // (DynamicTypeFactory, eski AQN gerekçesi) için de doğru ve tekil.
        // PERFORMANS: argüman imzası artık string değil ArgTypeKey (4 argümana kadar allocation yok).
        private readonly record struct CacheKey(Type Type, string Method, string Kind, Type ReturnType, ArgTypeKey Args, bool NonPublic);

        private CachedMethod BuildCachedFunc<TReturn>(string methodName, object[]? sampleArgs)
        {
            var methodInfo = GetMethodInfo(methodName, sampleArgs);
            var instanceParam = Expression.Parameter(typeof(object), "instance");
            var argsParam = Expression.Parameter(typeof(object[]), "args");
            var call = BuildCallExpression(methodInfo, instanceParam, argsParam, sampleArgs?.Length ?? methodInfo.GetParameters().Length);

            Expression body = call;
            if (methodInfo.ReturnType != typeof(void) && methodInfo.ReturnType != typeof(TReturn))
            {
                // Bkz. DSO.Core.Evoker.Plugins çalışmasında bulunan hata: Type.IsAssignableFrom
                // value type -> object/interface için boxing sayesinde "assignable" der, ama
                // Expression.Lambda value type'tan object'e implicit boxing conversion eklemez.
                // Expression.Convert hem boxing'i hem reference upcast/downcast'ı doğru ele alır.
                body = Expression.Convert(call, typeof(TReturn));
            }

            var lambda = Expression.Lambda<Func<object?, object[], TReturn>>(body, instanceParam, argsParam);
            return new CachedMethod(lambda.Compile(), methodInfo.IsStatic);
        }

        private CachedMethod BuildCachedAction(string methodName, object[]? sampleArgs)
        {
            var methodInfo = GetMethodInfo(methodName, sampleArgs);
            var instanceParam = Expression.Parameter(typeof(object), "instance");
            var argsParam = Expression.Parameter(typeof(object[]), "args");
            var call = BuildCallExpression(methodInfo, instanceParam, argsParam, sampleArgs?.Length ?? methodInfo.GetParameters().Length);

            var lambda = Expression.Lambda<Action<object?, object[]>>(call, instanceParam, argsParam);
            return new CachedMethod(lambda.Compile(), methodInfo.IsStatic);
        }

        // --- Tipli delegate'ler (object[] ve boxing YOK) ---
        // GetFunc<TReturn>(name) object[] alır: her çağrıda dizi + değer tipleri için boxing. Sıkı döngüler
        // için tipli sürümler: argüman ve dönüş tipleri derleme zamanında bilinir, derlenmiş delegate
        // parametreleri DOĞRUDAN metoda geçirir. Overload seçimi T1..Tn tiplerine göre (FindMethod ile aynı
        // kural); Tn metodun parametre tipinden farklıysa (ör. int -> long, türetilmiş -> taban) dönüşüm
        // derlenir, mümkün değilse InvalidCastException. Optional parametreler verilmezse varsayılanla dolar.
        // Instance: SetInstance verilmişse delegate'e bağlanır; verilmemişse her çağrıda (Invoke gibi) çözülür.

        public Func<TResult> GetTypedFunc<TResult>(string methodName)
        {
            var (inv, st) = TypedInvoker<Func<object?, TResult>>(methodName, Type.EmptyTypes, typeof(TResult));
            if (TryBind(st, out var o)) return () => inv(o);
            return () => inv(ResolveInstance());
        }

        public Func<T1, TResult> GetTypedFunc<T1, TResult>(string methodName)
        {
            var (inv, st) = TypedInvoker<Func<object?, T1, TResult>>(methodName, new[] { typeof(T1) }, typeof(TResult));
            if (TryBind(st, out var o)) return a => inv(o, a);
            return a => inv(ResolveInstance(), a);
        }

        public Func<T1, T2, TResult> GetTypedFunc<T1, T2, TResult>(string methodName)
        {
            var (inv, st) = TypedInvoker<Func<object?, T1, T2, TResult>>(methodName, new[] { typeof(T1), typeof(T2) }, typeof(TResult));
            if (TryBind(st, out var o)) return (a, b) => inv(o, a, b);
            return (a, b) => inv(ResolveInstance(), a, b);
        }

        public Func<T1, T2, T3, TResult> GetTypedFunc<T1, T2, T3, TResult>(string methodName)
        {
            var (inv, st) = TypedInvoker<Func<object?, T1, T2, T3, TResult>>(methodName, new[] { typeof(T1), typeof(T2), typeof(T3) }, typeof(TResult));
            if (TryBind(st, out var o)) return (a, b, c) => inv(o, a, b, c);
            return (a, b, c) => inv(ResolveInstance(), a, b, c);
        }

        public Func<T1, T2, T3, T4, TResult> GetTypedFunc<T1, T2, T3, T4, TResult>(string methodName)
        {
            var (inv, st) = TypedInvoker<Func<object?, T1, T2, T3, T4, TResult>>(methodName, new[] { typeof(T1), typeof(T2), typeof(T3), typeof(T4) }, typeof(TResult));
            if (TryBind(st, out var o)) return (a, b, c, d) => inv(o, a, b, c, d);
            return (a, b, c, d) => inv(ResolveInstance(), a, b, c, d);
        }

        public Action GetTypedAction(string methodName)
        {
            var (inv, st) = TypedInvoker<Action<object?>>(methodName, Type.EmptyTypes, null);
            if (TryBind(st, out var o)) return () => inv(o);
            return () => inv(ResolveInstance());
        }

        public Action<T1> GetTypedAction<T1>(string methodName)
        {
            var (inv, st) = TypedInvoker<Action<object?, T1>>(methodName, new[] { typeof(T1) }, null);
            if (TryBind(st, out var o)) return a => inv(o, a);
            return a => inv(ResolveInstance(), a);
        }

        public Action<T1, T2> GetTypedAction<T1, T2>(string methodName)
        {
            var (inv, st) = TypedInvoker<Action<object?, T1, T2>>(methodName, new[] { typeof(T1), typeof(T2) }, null);
            if (TryBind(st, out var o)) return (a, b) => inv(o, a, b);
            return (a, b) => inv(ResolveInstance(), a, b);
        }

        public Action<T1, T2, T3> GetTypedAction<T1, T2, T3>(string methodName)
        {
            var (inv, st) = TypedInvoker<Action<object?, T1, T2, T3>>(methodName, new[] { typeof(T1), typeof(T2), typeof(T3) }, null);
            if (TryBind(st, out var o)) return (a, b, c) => inv(o, a, b, c);
            return (a, b, c) => inv(ResolveInstance(), a, b, c);
        }

        public Action<T1, T2, T3, T4> GetTypedAction<T1, T2, T3, T4>(string methodName)
        {
            var (inv, st) = TypedInvoker<Action<object?, T1, T2, T3, T4>>(methodName, new[] { typeof(T1), typeof(T2), typeof(T3), typeof(T4) }, null);
            if (TryBind(st, out var o)) return (a, b, c, d) => inv(o, a, b, c, d);
            return (a, b, c, d) => inv(ResolveInstance(), a, b, c, d);
        }

        // Static metot -> instance null (bağlı); SetInstance varsa o nesne (bağlı); yoksa her çağrıda çözülür.
        private bool TryBind(bool isStatic, out object? instance)
        {
            instance = isStatic ? null : _existingInstance;
            return isStatic || instance != null;
        }

        private (TDelegate Invoker, bool IsStatic) TypedInvoker<TDelegate>(string methodName, Type[] argTypes, Type? resultType)
            where TDelegate : Delegate
        {
            var key = new CacheKey(_type, methodName, resultType == null ? KindTypedAction : KindTypedFunc,
                resultType ?? typeof(void), ArgTypeKey.FromTypes(argTypes), _includeNonPublic);
            var cm = Cache.GetOrAdd(key, k => BuildTyped(typeof(TDelegate), k.Method, argTypes, resultType));
            return ((TDelegate)cm.Invoker, cm.IsStatic);
        }

        private CachedMethod BuildTyped(Type delegateType, string methodName, Type[] argTypes, Type? resultType)
        {
            var methodInfo = FindMethodByTypes(methodName, argTypes);
            var parameters = methodInfo.GetParameters();
            var instanceParam = Expression.Parameter(typeof(object), "instance");
            var argParams = argTypes.Select((t, i) => Expression.Parameter(t, "a" + i)).ToArray();

            var callArgs = new Expression[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                var pt = parameters[i].ParameterType;
                if (pt.IsByRef)
                    throw new NotSupportedException($"[EvokerBuilder] '{methodInfo.Name}' metodunun '{parameters[i].Name}' parametresi ref/out - tipli delegate ile desteklenmez.");
                if (i >= argParams.Length) { callArgs[i] = DefaultValueExpression(parameters[i]); continue; }
                callArgs[i] = ConvertOrThrow(argParams[i], pt, methodInfo, $"{i + 1}. argüman");
            }

            Expression? instance = methodInfo.IsStatic ? null : Expression.Convert(instanceParam, methodInfo.DeclaringType!);
            Expression body = Expression.Call(instance, methodInfo, callArgs);

            if (resultType != null)
            {
                if (methodInfo.ReturnType == typeof(void))
                    throw new InvalidOperationException($"[EvokerBuilder] '{_type.Name}.{methodInfo.Name}' void döndürüyor - GetTypedAction kullanın.");
                body = ConvertOrThrow(body, resultType, methodInfo, "dönüş değeri");
            }

            var lambda = Expression.Lambda(delegateType, body, new[] { instanceParam }.Concat(argParams));
            return new CachedMethod(lambda.Compile(), methodInfo.IsStatic);
        }

        private Expression ConvertOrThrow(Expression e, Type to, MethodInfo m, string what)
        {
            if (e.Type == to) return e;
            try { return Expression.Convert(e, to); }
            catch (InvalidOperationException)
            {
                throw new InvalidCastException(
                    $"[EvokerBuilder] '{_type.Name}.{m.Name}': {what} için '{e.Type.Name}' -> '{to.Name}' dönüşümü yok.");
            }
        }

        private MethodInfo GetMethodInfo(string methodName, object[]? sampleArgs) => FindMethod(methodName, sampleArgs);

        /// <summary>
        /// Invoke/Execute/GetFunc'ın kullandığı metot seçimi - dışarıdan da (ör. DSO.Core.Evoker.Plugins'in
        /// dönüş şeklini önceden bilmesi gereken katmanları) AYNI kuralla çözebilsin diye public.
        /// Kurallar:
        ///   1) İsim önce birebir (Ordinal) aranır; hiç yoksa büyük/küçük harf DUYARSIZ aranır (VB.NET
        ///      case-insensitive bir dil - "add" ile "Add" çağrılabilir).
        ///   2) sampleArgs null ise ilk aday döner (eski davranış).
        ///   3) Parametre sayısı argüman sayısına EŞİT adaylar önceliklidir; yoksa, argüman sayısından
        ///      FAZLA parametresi olup fazlalıkların HEPSİ optional olan adaylar (VB.NET "Optional",
        ///      C# "= varsayılan") kullanılır - eksik argümanlar çağrıda metodun varsayılanlarıyla doldurulur.
        ///   4) Birden fazla aday varsa argüman tiplerine göre: önce birebir tip, sonra atanabilir tip.
        /// Uygun aday yoksa MissingMethodException (mevcut imzaları listeleyerek).
        /// </summary>
        public MethodInfo FindMethod(string methodName, object?[]? sampleArgs)
        {
            if (sampleArgs == null) return FindMethodCore(methodName, null);
            var types = new Type?[sampleArgs.Length];
            for (int i = 0; i < types.Length; i++) types[i] = sampleArgs[i]?.GetType();
            return FindMethodCore(methodName, types);
        }

        /// <summary>FindMethod ile aynı kural; argüman DEĞERLERİ yerine TİPLERİ verilir (null eleman = null argüman).</summary>
        public MethodInfo FindMethodByTypes(string methodName, Type?[] argTypes)
            => FindMethodCore(methodName, argTypes ?? throw new ArgumentNullException(nameof(argTypes)));

        private MethodInfo FindMethodCore(string methodName, Type?[]? argTypes)
        {
            if (string.IsNullOrWhiteSpace(methodName))
                throw new ArgumentException("methodName boş olamaz.", nameof(methodName));

            var methods = MethodsNamed(methodName);
            if (methods.Count == 0)
                throw new MissingMethodException($"[EvokerEngine] '{_type.Name}' üzerinde '{methodName}' metodu bulunamadı.");

            if (argTypes == null)
                return methods[0];

            int n = argTypes.Length;
            var candidates = CandidatesFor(methods, n);

            if (candidates.Count == 0)
            {
                var sigs = string.Join("; ", methods.Select(m =>
                    $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => (p.IsOptional ? "[opt] " : "") + p.ParameterType.Name))})"));
                throw new MissingMethodException(
                    $"[EvokerEngine] '{_type.Name}.{methodName}' için {n} argümanla çağrılabilen bir overload yok. Mevcut: {sigs}");
            }

            if (candidates.Count == 1)
                return candidates[0];

            var exactMatch = candidates.FirstOrDefault(m => MatchesArgTypes(m, argTypes, exact: true));
            if (exactMatch != null) return exactMatch;

            var compatibleMatch = candidates.FirstOrDefault(m => MatchesArgTypes(m, argTypes, exact: false));
            if (compatibleMatch != null) return compatibleMatch;

            return candidates[0];
        }

        /// <summary>
        /// FindMethod'un 1. ve 3. kuralına göre n argümanla çağrılabilecek adaylar (tip bakılmadan): önce
        /// parametre sayısı birebir n olanlar, yoksa fazlası optional olanlar. Argüman DEĞERLERİ elde
        /// olmadan (ör. sadece sayısı biliniyorsa) aynı kuralla ön-eleme yapmak isteyen katmanlar için.
        /// </summary>
        public IReadOnlyList<MethodInfo> FindMethodCandidates(string methodName, int argCount) =>
            CandidatesFor(MethodsNamed(methodName), argCount);

        private List<MethodInfo> MethodsNamed(string methodName)
        {
            var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
            if (_includeNonPublic) flags |= BindingFlags.NonPublic;

            var all = _type.GetMethods(flags);
            var methods = all.Where(m => m.Name == methodName).ToList();
            if (methods.Count == 0)
                methods = all.Where(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase)).ToList();
            return methods;
        }

        private static List<MethodInfo> CandidatesFor(List<MethodInfo> methods, int n)
        {
            var exact = methods.Where(m => m.GetParameters().Length == n).ToList();
            return exact.Count > 0
                ? exact
                : methods.Where(m => AcceptsArgCount(m, n)).OrderBy(m => m.GetParameters().Length).ToList();
        }

        /// <summary>Metot n argümanla çağrılabilir mi: n &lt;= parametre sayısı ve n'den sonraki parametrelerin hepsi optional.</summary>
        public static bool AcceptsArgCount(MethodInfo method, int n)
        {
            var ps = method.GetParameters();
            if (n > ps.Length) return false;
            for (int i = n; i < ps.Length; i++)
                if (!ps[i].IsOptional) return false;
            return true;
        }

        private static bool MatchesArgTypes(MethodInfo method, Type?[] argTypes, bool exact)
        {
            var parameters = method.GetParameters();
            // Sadece VERİLEN argümanlar karşılaştırılır - geri kalan parametreler optional (varsayılanla dolacak).
            for (int i = 0; i < argTypes.Length && i < parameters.Length; i++)
            {
                var paramType = parameters[i].ParameterType;
                if (paramType.IsByRef) paramType = paramType.GetElementType()!;

                var argType = argTypes[i];
                if (argType == null)
                {
                    if (paramType.IsValueType && Nullable.GetUnderlyingType(paramType) == null)
                        return false;
                    continue;
                }

                if (exact)
                {
                    if (paramType != argType) return false;
                }
                else
                {
                    if (!paramType.IsAssignableFrom(argType)) return false;
                }
            }
            return true;
        }

        private static MethodCallExpression BuildCallExpression(MethodInfo methodInfo, ParameterExpression instanceParam, ParameterExpression argsParam, int suppliedCount)
        {
            var parameters = methodInfo.GetParameters();
            var convertedArgs = new Expression[parameters.Length];

            for (int i = 0; i < parameters.Length; i++)
            {
                var paramType = parameters[i].ParameterType;

                if (paramType.IsByRef)
                {
                    // ÖNEMLİ (bulundu, önceden sessizce yanlıştı): burada eskiden paramType'ı
                    // byref'sizleştirip Expression.Convert ile devam ediyorduk. Bu DERLENİYORDU
                    // ve ÇALIŞIYORDU (hata fırlatmıyordu) ama LINQ Expression derleyicisi byref
                    // argümanı için görünmez bir GEÇİCİ DEĞİŞKEN oluşturup çağrıyı onun üzerinden
                    // yapıyor, sonra o geçici değişkeni SESSİZCE ATIYORDU - yani `out`/`ref` ile
                    // dönen değer çağırana ASLA ulaşmıyordu. Artık açıkça reddediyoruz.
                    throw new NotSupportedException(
                        $"[EvokerBuilder] '{methodInfo.Name}' metodunun '{parameters[i].Name}' parametresi " +
                        "ref/out. EvokerBuilder'ın object[] tabanlı çağrı sözleşmesi ref/out DEĞERLERİNİ " +
                        "ÇAĞIRANA GERİ TAŞIYAMAZ (önceden sessizce kaybediyordu, şimdi açıkça reddediyoruz). " +
                        "Bunun yerine düz reflection kullanın: " +
                        "'type.GetMethod(name).Invoke(instance, args)' - bu, args dizisindeki out/ref " +
                        "slotlarını doğru şekilde günceller.");
                }

                if (i >= suppliedCount)
                {
                    // Verilmemiş optional parametre (bkz. FindMethod kural 3) - metodun kendi varsayılanı.
                    convertedArgs[i] = DefaultValueExpression(parameters[i]);
                    continue;
                }

                var arrayAccess = Expression.ArrayIndex(argsParam, Expression.Constant(i));
                convertedArgs[i] = Expression.Convert(arrayAccess, paramType);
            }

            Expression? instance = null;
            if (!methodInfo.IsStatic)
            {
                // Instance artık bir Expression.Constant DEĞİL, runtime'da geçilen bir
                // parametre (instanceParam). Bu sayede derlenmiş delegate her seferinde
                // aynı nesneyi/tipi kullanmak zorunda kalmıyor.
                instance = Expression.Convert(instanceParam, methodInfo.DeclaringType!);
            }

            return Expression.Call(instance, methodInfo, convertedArgs);
        }

        // Optional parametrenin varsayılan değeri. DefaultValue null/DBNull ise (ör. "= null", "= default",
        // ya da sadece [Optional] işaretli) default(T); enum varsayılanları metadata'da alttaki sayı olarak
        // durur, Convert ile enum'a çevrilir. decimal/DateTime varsayılanları (Decimal/DateTimeConstant
        // attribute'ları) ParameterInfo.DefaultValue tarafından zaten doğru okunur.
        private static Expression DefaultValueExpression(ParameterInfo p)
        {
            var t = p.ParameterType;
            object? dv = p.HasDefaultValue ? p.DefaultValue : null;
            if (dv == null || dv is DBNull || dv == Type.Missing)
                return Expression.Default(t);
            return Expression.Convert(Expression.Constant(dv, dv.GetType()), t);
        }

        // Instance her ÇAĞRIDA (cache'in dışında) burada çözülüyor:
        // - SetInstance verilmişse o nesne kullanılır
        // - SetConstructor verilmişse uygun constructor ile YENİ bir nesne üretilir
        // - Hiçbiri verilmemişse parametresiz constructor ile YENİ bir nesne üretilir
        // (Bu, orijinal koddaki "her invoke'ta new instance" davranışını korur.)
        private object? ResolveInstance()
        {
            if (_existingInstance != null)
                return _existingInstance;

            if (_constructorArgs != null && _constructorArgs.Length > 0)
            {
                var ctorTypes = _constructorArgs.Select(x => x.GetType()).ToArray();
                var ctor = _type.GetConstructor(ctorTypes)
                    ?? throw new MissingMethodException(
                        $"[EvokerEngine] '{_type.Name}' için uyumlu constructor bulunamadı (Args: {string.Join(", ", ctorTypes.Select(t => t.Name))}).");
                return ctor.Invoke(_constructorArgs);
            }

            try
            {
                return DynamicEntityAccessor.GetConstructor(_type)();
            }
            catch (MissingMethodException)
            {
                throw new MissingMethodException(
                    $"[EvokerEngine] '{_type.Name}' türünün parametresiz constructor'ı yok. SetInstance() veya SetConstructor() kullanın.");
            }
        }

        private sealed class CachedMethod
        {
            public readonly Delegate Invoker;
            public readonly bool IsStatic;

            public CachedMethod(Delegate invoker, bool isStatic)
            {
                Invoker = invoker;
                IsStatic = isStatic;
            }
        }

        private static readonly ConcurrentDictionary<CacheKey, CachedMethod> Cache = new();

        /// <summary>
        /// Bu tipe ait TÜM derlenmiş delegate'leri statik cache'ten çıkarır. Bir plugin'in
        /// AssemblyLoadContext'i unload edilecekse ŞART: cache'teki delegate'ler plugin tiplerine güçlü
        /// referans tutar ve context'in bellekten atılmasını engeller.
        /// </summary>
        public static void ForgetType(Type type)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            foreach (var key in Cache.Keys)
                if (key.Type == type || Involves(key.ReturnType, type) || key.Args.Involves(t => Involves(t, type)))
                    Cache.TryRemove(key, out _);
        }

        // t, target'ın kendisi ya da onu (dizi elemanı / generic argüman olarak - ör. List<Point>, Task<Point>) içeriyor mu.
        private static bool Involves(Type t, Type target) =>
            t == target
            || (t.HasElementType && Involves(t.GetElementType()!, target))
            || (t.IsGenericType && !t.IsGenericTypeDefinition && t.GetGenericArguments().Any(g => Involves(g, target)));

        /// <summary>Statik cache'te bu tipe ait kaç derlenmiş delegate var (tanılama/test).</summary>
        public static int CachedCountFor(Type type) => Cache.Keys.Count(k => k.Type == type);
    }
}