using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace DSO.Core.Evoker
{
    public static class EvokerEngine
    {
        // Normal (kaldırılamaz) tipler: kalıcı cache.
        private static readonly ConcurrentDictionary<string, Type> TypeCache = new(StringComparer.Ordinal);

        // Kaldırılabilir (collectible AssemblyLoadContext - ör. in-process plugin) tipler: ZAYIF referansla.
        // Güçlü referans plugin'in context'ini bellekte tutar (unload olamaz) ve reload sonrası ESKİ tipi döndürürdü.
        // Context Unload edildiğinde (Unloading event'i) o context'in girdileri hemen silinir.
        private static readonly ConcurrentDictionary<string, WeakReference<Type>> CollectibleCache = new(StringComparer.Ordinal);
        private static readonly ConditionalWeakTable<AssemblyLoadContext, object> HookedContexts = new();

        /// <summary>
        /// İsimden tip çözer. Sıra: Type.GetType (assembly-qualified ad), sonra yüklü tüm assembly'lerde
        /// 1) FullName birebir, 2) FullName büyük/küçük harf duyarsız, 3) kısa ad birebir, 4) kısa ad duyarsız.
        /// İlk eşleşen seviyede birden fazla FARKLI tip varsa (ör. iki plugin'de "Result" sınıfı, ya da aynı
        /// plugin'in iki context'teki kopyası) tahmin etmez: AmbiguousMatchException ile adayları listeler -
        /// tam ad (namespace dahil) verilerek çözülür.
        ///
        /// Önceki sürüme göre düzeltilenler: kısmen yüklenebilen assembly'lerde (ReflectionTypeLoadException)
        /// tüm aramanın düşmesi; aynı ada sahip tiplerde sessizce rastgele birini dönmesi; FullName'i null olan
        /// tiplerde NullReferenceException; plugin tiplerinin cache'te kalıp context unload'unu engellemesi.
        /// </summary>
        public static Type ResolveType(string className)
        {
            if (string.IsNullOrWhiteSpace(className))
                throw new ArgumentException("className boş olamaz.", nameof(className));

            if (TypeCache.TryGetValue(className, out var cached))
                return cached;
            if (CollectibleCache.TryGetValue(className, out var weak) && weak.TryGetTarget(out var live))
                return live;

            var type = FindType(className);
            if (type.Assembly.IsCollectible)
                CacheCollectible(className, type);
            else
                TypeCache.TryAdd(className, type);
            return type;
        }

        /// <summary>İsim cache'ini temizler (ör. yeni assembly'ler yüklendiyse ve kısa ad artık belirsizse).</summary>
        public static void ClearTypeCache()
        {
            TypeCache.Clear();
            CollectibleCache.Clear();
        }

        private static void CacheCollectible(string name, Type type)
        {
            CollectibleCache[name] = new WeakReference<Type>(type);
            var ctx = AssemblyLoadContext.GetLoadContext(type.Assembly);
            if (ctx == null) return;
            lock (HookedContexts)
            {
                if (HookedContexts.TryGetValue(ctx, out _)) return;
                HookedContexts.Add(ctx, new object());
            }
            ctx.Unloading += OnContextUnloading;
        }

        private static void OnContextUnloading(AssemblyLoadContext ctx)
        {
            foreach (var kv in CollectibleCache)
            {
                if (!kv.Value.TryGetTarget(out var t) || AssemblyLoadContext.GetLoadContext(t.Assembly) == ctx)
                    CollectibleCache.TryRemove(kv.Key, out _);
            }
        }

        private static Type FindType(string name)
        {
            var direct = Type.GetType(name, throwOnError: false);
            if (direct != null) return direct;

            var all = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !IsUnloading(a))
                .SelectMany(SafeGetTypes)
                .ToList();

            var levels = new Func<Type, bool>[]
            {
                t => string.Equals(t.FullName, name, StringComparison.Ordinal),
                t => string.Equals(t.FullName, name, StringComparison.OrdinalIgnoreCase),
                t => string.Equals(t.Name, name, StringComparison.Ordinal),
                t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)
            };

            foreach (var match in levels)
            {
                var found = all.Where(match).Distinct().ToList();
                if (found.Count == 1) return found[0];
                if (found.Count > 1)
                {
                    var list = string.Join("; ", found.Take(10).Select(t =>
                        $"{t.FullName} ({t.Assembly.GetName().Name}, context: {AssemblyLoadContext.GetLoadContext(t.Assembly)?.Name ?? "?"})"));
                    throw new AmbiguousMatchException(
                        $"[EvokerEngine] '{name}' adı birden fazla tiple eşleşiyor ({found.Count}): {list}. " +
                        "Namespace dahil tam adı ya da assembly-qualified adı verin.");
                }
            }

            throw new TypeLoadException($"[EvokerEngine] '{name}' türü bulunamadı.");
        }

        // Kısmen yüklenebilen assembly (bir tipinin bağımlılığı eksik / karışık içerik): çözülebilen tipleri
        // kullan, assembly'yi tamamen düşürme. Dinamik/özel assembly'lerde GetTypes desteklenmezse boş geç.
        private static IEnumerable<Type> SafeGetTypes(Assembly a)
        {
            try { return a.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null)!; }
            catch { return Array.Empty<Type>(); }
        }

        private static bool IsUnloading(Assembly a)
        {
            if (!a.IsCollectible) return false;
            var ctx = AssemblyLoadContext.GetLoadContext(a);
            return ctx != null && !AssemblyLoadContext.All.Contains(ctx);
        }

        /// <summary>
        /// Parametreli constructor alan tipler için EvokerBuilder başlatır.
        /// </summary>
        public static EvokerBuilder Target<TEntity>(params object[] constructorArgs)
        {
            return new EvokerBuilder(typeof(TEntity)).SetConstructor(constructorArgs);
        }

        public static EvokerBuilder Target(string className, params object[] constructorArgs)
        {
            return new EvokerBuilder(ResolveType(className)).SetConstructor(constructorArgs);
        }

        public static EvokerBuilder Target(Type type, params object[] constructorArgs)
        {
            return new EvokerBuilder(type).SetConstructor(constructorArgs);
        }

        // --- Senkron Tetikleme ---
        public static object? Invoke(string className, string methodName, bool includeNonPublic = false, params object[] args)
        {
            return new EvokerBuilder(ResolveType(className), includeNonPublic).Invoke(methodName, args);
        }

        public static TReturn? InvokePublic<TEntity, TReturn>(string methodName, params object[] args)
        {
            return new EvokerBuilder(typeof(TEntity), false).Invoke<TReturn>(methodName, args);
        }

        public static TReturn? InvokePrivate<TEntity, TReturn>(string methodName, params object[] args)
        {
            return new EvokerBuilder(typeof(TEntity), true).Invoke<TReturn>(methodName, args);
        }

        public static void Execute(string className, string methodName, bool includeNonPublic = false, params object[] args)
        {
            new EvokerBuilder(ResolveType(className), includeNonPublic).Execute(methodName, args);
        }

        public static void ExecutePublic<TEntity>(string methodName, params object[] args)
        {
            new EvokerBuilder(typeof(TEntity), false).Execute(methodName, args);
        }

        public static void ExecutePrivate<TEntity>(string methodName, params object[] args)
        {
            new EvokerBuilder(typeof(TEntity), true).Execute(methodName, args);
        }

        // --- Asenkron Tetikleme ---
        public static async Task ExecutePublicAsync(string className, string methodName, params object[] args)
        {
            await new EvokerBuilder(ResolveType(className), false).ExecuteAsync(methodName, args);
        }

        public static async Task ExecutePrivateAsync(string className, string methodName, params object[] args)
        {
            await new EvokerBuilder(ResolveType(className), true).ExecuteAsync(methodName, args);
        }

        public static async Task ExecutePublicAsync<TEntity>(string methodName, params object[] args)
        {
            await new EvokerBuilder(typeof(TEntity), false).ExecuteAsync(methodName, args);
        }

        public static async Task ExecutePrivateAsync<TEntity>(string methodName, params object[] args)
        {
            await new EvokerBuilder(typeof(TEntity), true).ExecuteAsync(methodName, args);
        }

        public static async Task<TReturn?> InvokePublicAsync<TEntity, TReturn>(string methodName, params object[] args)
        {
            return await new EvokerBuilder(typeof(TEntity), false).InvokeAsync<TReturn>(methodName, args);
        }

        public static async Task<TReturn?> InvokePrivateAsync<TEntity, TReturn>(string methodName, params object[] args)
        {
            return await new EvokerBuilder(typeof(TEntity), true).InvokeAsync<TReturn>(methodName, args);
        }
    }
}