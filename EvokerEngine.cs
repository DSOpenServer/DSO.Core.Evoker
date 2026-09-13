using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;

namespace DSO.Core.Evoker
{
    public static class EvokerEngine
    {
        private static readonly ConcurrentDictionary<string, Type> TypeCache = new();

        public static Type ResolveType(string className)
        {
            return TypeCache.GetOrAdd(className, name =>
            {
                var type = Type.GetType(name);
                if (type != null) return type;

                type = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => a.GetTypes())
                    .FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                                      || t.FullName!.Equals(name, StringComparison.OrdinalIgnoreCase));

                return type ?? throw new TypeLoadException($"[EvokerEngine] '{name}' türü bulunamadı.");
            });
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

        public static async Task<TReturn?> InvokePublicAsync<TEntity, TReturn>(string methodName,  params object[] args)
        {
            return await new EvokerBuilder(typeof(TEntity), false).InvokeAsync<TReturn>(methodName, args);
        }

        public static async Task<TReturn?> InvokePrivateAsync<TEntity, TReturn>(string methodName, params object[] args)
        {
            return await new EvokerBuilder(typeof(TEntity), true).InvokeAsync<TReturn>(methodName, args);
        }
    }
}