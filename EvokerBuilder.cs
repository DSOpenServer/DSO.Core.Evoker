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

        public object? Invoke(string methodName, params object[] args)
        {
            var func = GetFunc<object>(methodName, args);
            return func(args);
        }

        public TReturn? Invoke<TReturn>(string methodName, params object[] args)
        {
            var func = GetFunc<TReturn>(methodName, args);
            return func(args);
        }

        public void Execute(string methodName, params object[] args)
        {
            var action = GetAction(methodName, args);
            action(args);
        }

        public async Task ExecuteAsync(string methodName, params object[] args)
        {
            var func = GetFunc<Task>(methodName, args);
            var task = func(args);
            if (task != null) await task;
        }

        public async Task<TReturn?> InvokeAsync<TReturn>(string methodName, params object[] args)
        {
            var func = GetFunc<Task<TReturn>>(methodName, args);
            var task = func(args);
            return task != null ? await task : default;
        }

        // --- Delegate Üretimi ve Caching ---
        // ÖNEMLİ: Cache'lenen delegate artık instance/constructor argümanlarını İÇİNDE BARINDIRMIYOR.
        // Instance her çağrıda ResolveInstance() ile ayrı üretilip delegate'e PARAMETRE olarak geçiliyor.
        // Böylece aynı (Type, Method, ReturnType) kombinasyonuna sahip farklı EvokerBuilder
        // örnekleri (farklı SetInstance/SetConstructor ile) birbirinin sonucunu ezmiyor.
        public Func<object[], TReturn> GetFunc<TReturn>(string methodName, object[]? sampleArgs = null)
        {
            string cacheKey = BuildCacheKey(methodName, "Func", typeof(TReturn).Name, sampleArgs);

            var cached = Cache.GetOrAdd(cacheKey, _ => BuildCachedFunc<TReturn>(methodName, sampleArgs));
            var typedInvoker = (Func<object?, object[], TReturn>)cached.Invoker;

            return args =>
            {
                object? instance = cached.IsStatic ? null : ResolveInstance();
                return typedInvoker(instance, args);
            };
        }

        public Action<object[]> GetAction(string methodName, object[]? sampleArgs = null)
        {
            string cacheKey = BuildCacheKey(methodName, "Action", "void", sampleArgs);

            var cached = Cache.GetOrAdd(cacheKey, _ => BuildCachedAction(methodName, sampleArgs));
            var typedInvoker = (Action<object?, object[]>)cached.Invoker;

            return args =>
            {
                object? instance = cached.IsStatic ? null : ResolveInstance();
                typedInvoker(instance, args);
            };
        }

        private string BuildCacheKey(string methodName, string kind, string returnTypeName, object[]? sampleArgs)
        {
            // _type.FullName YERİNE AssemblyQualifiedName kullanılıyor:
            // Reflection.Emit ile üretilen dinamik tipler (bkz. DynamicTypeFactory) aynı isme
            // ("DynamicCustomer" gibi) sahip ama BAMBAŞKA Type nesneleri olabilir. FullName bu
            // durumda çakışıp yanlış (önceki tipe ait) delegate'in cache'ten dönmesine sebep olurdu.
            // AssemblyQualifiedName, dinamik assembly adının içindeki GUID sayesinde tekil kalır.
            string typeIdentity = _type.AssemblyQualifiedName ?? _type.FullName ?? _type.Name;

            // Argüman TİPLERİ de anahtara dahil ediliyor (sadece sayı değil): aksi halde
            // Calc(int,int) ve Calc(string,string) gibi aynı isim + aynı parametre sayısına
            // sahip overload'lar aynı cache key'i paylaşıp birbirinin delegate'ini çalıştırırdı.
            string argSignature = sampleArgs == null
                ? "null"
                : string.Join(",", sampleArgs.Select(a => a?.GetType().Name ?? "null"));

            return $"{typeIdentity}.{methodName}:{kind}<{returnTypeName}>:ArgTypes=({argSignature}):NonPublic={_includeNonPublic}";
        }

        private CachedMethod BuildCachedFunc<TReturn>(string methodName, object[]? sampleArgs)
        {
            var methodInfo = GetMethodInfo(methodName, sampleArgs);
            var instanceParam = Expression.Parameter(typeof(object), "instance");
            var argsParam = Expression.Parameter(typeof(object[]), "args");
            var call = BuildCallExpression(methodInfo, instanceParam, argsParam);

            Expression body = call;
            if (methodInfo.ReturnType != typeof(void) && !typeof(TReturn).IsAssignableFrom(methodInfo.ReturnType))
            {
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
            var call = BuildCallExpression(methodInfo, instanceParam, argsParam);

            var lambda = Expression.Lambda<Action<object?, object[]>>(call, instanceParam, argsParam);
            return new CachedMethod(lambda.Compile(), methodInfo.IsStatic);
        }

        private MethodInfo GetMethodInfo(string methodName, object[]? sampleArgs)
        {
            var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
            if (_includeNonPublic) flags |= BindingFlags.NonPublic;

            var methods = _type.GetMethods(flags).Where(m => m.Name == methodName).ToList();

            if (!methods.Any())
                throw new MissingMethodException($"[EvokerEngine] '{_type.Name}' üzerinde '{methodName}' metodu bulunamadı.");

            if (sampleArgs == null)
                return methods.First();

            var candidatesByCount = methods.Where(m => m.GetParameters().Length == sampleArgs.Length).ToList();

            if (candidatesByCount.Count == 0)
                return methods.First(); // eski davranışla uyumluluk için fallback

            if (candidatesByCount.Count == 1)
                return candidatesByCount[0];

            // Birden fazla overload aynı parametre sayısına sahipse, argüman tiplerine göre
            // en uygun eşleşmeyi bulmaya çalışıyoruz (eskiden sadece ilk bulunan seçiliyordu,
            // bu da overload'lar arasında öngörülemez sonuçlara yol açabiliyordu).
            var exactMatch = candidatesByCount.FirstOrDefault(m =>
                MatchesArgTypes(m, sampleArgs, exact: true));
            if (exactMatch != null) return exactMatch;

            var compatibleMatch = candidatesByCount.FirstOrDefault(m =>
                MatchesArgTypes(m, sampleArgs, exact: false));
            if (compatibleMatch != null) return compatibleMatch;

            return candidatesByCount[0];
        }

        private static bool MatchesArgTypes(MethodInfo method, object[] sampleArgs, bool exact)
        {
            var parameters = method.GetParameters();
            for (int i = 0; i < parameters.Length; i++)
            {
                var paramType = parameters[i].ParameterType;
                if (paramType.IsByRef) paramType = paramType.GetElementType()!;

                var arg = sampleArgs[i];
                if (arg == null)
                {
                    if (paramType.IsValueType && Nullable.GetUnderlyingType(paramType) == null)
                        return false;
                    continue;
                }

                if (exact)
                {
                    if (paramType != arg.GetType()) return false;
                }
                else
                {
                    if (!paramType.IsAssignableFrom(arg.GetType())) return false;
                }
            }
            return true;
        }

        private static MethodCallExpression BuildCallExpression(MethodInfo methodInfo, ParameterExpression instanceParam, ParameterExpression argsParam)
        {
            var parameters = methodInfo.GetParameters();
            var convertedArgs = new Expression[parameters.Length];

            for (int i = 0; i < parameters.Length; i++)
            {
                var paramType = parameters[i].ParameterType;
                if (paramType.IsByRef) paramType = paramType.GetElementType()!;

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
                return Activator.CreateInstance(_type);
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

        private static readonly ConcurrentDictionary<string, CachedMethod> Cache = new();
    }
}