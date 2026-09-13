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
        public Func<object[], TReturn> GetFunc<TReturn>(string methodName, object[]? sampleArgs = null)
        {
            string cacheKey = $"{_type.FullName}.{methodName}:Func<{typeof(TReturn).Name}>:NonPublic={_includeNonPublic}";

            return (Func<object[], TReturn>)Cache.GetOrAdd(cacheKey, _ =>
            {
                var methodInfo = GetMethodInfo(methodName, sampleArgs);
                var argsParam = Expression.Parameter(typeof(object[]), "args");
                var body = BuildCallExpression(methodInfo, argsParam);

                Expression finalBody = body;
                if (methodInfo.ReturnType != typeof(void) && !typeof(TReturn).IsAssignableFrom(methodInfo.ReturnType))
                {
                    finalBody = Expression.Convert(body, typeof(TReturn));
                }

                return Expression.Lambda<Func<object[], TReturn>>(finalBody, argsParam).Compile();
            });
        }

        public Action<object[]> GetAction(string methodName, object[]? sampleArgs = null)
        {
            string cacheKey = $"{_type.FullName}.{methodName}:Action:NonPublic={_includeNonPublic}";

            return (Action<object[]>)Cache.GetOrAdd(cacheKey, _ =>
            {
                var methodInfo = GetMethodInfo(methodName, sampleArgs);
                var argsParam = Expression.Parameter(typeof(object[]), "args");
                var body = BuildCallExpression(methodInfo, argsParam);

                return Expression.Lambda<Action<object[]>>(body, argsParam).Compile();
            });
        }

        private MethodInfo GetMethodInfo(string methodName, object[]? sampleArgs)
        {
            var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
            if (_includeNonPublic) flags |= BindingFlags.NonPublic;

            var methods = _type.GetMethods(flags).Where(m => m.Name == methodName).ToList();

            if (!methods.Any())
                throw new MissingMethodException($"[EvokerEngine] '{_type.Name}' üzerinde '{methodName}' metodu bulunamadı.");

            if (sampleArgs != null)
            {
                var match = methods.FirstOrDefault(m => m.GetParameters().Length == sampleArgs.Length);
                if (match != null) return match;
            }

            return methods.First();
        }

        private MethodCallExpression BuildCallExpression(MethodInfo methodInfo, ParameterExpression argsParam)
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
                // 1. Eğer var olan bir instance verildiyse onu Constant olarak kullan
                if (_existingInstance != null)
                {
                    instance = Expression.Constant(_existingInstance, _type);
                }
                // 2. Constructor argümanları verildiyse parametreli new çalıştır
                else if (_constructorArgs != null && _constructorArgs.Length > 0)
                {
                    var ctorTypes = _constructorArgs.Select(x => x.GetType()).ToArray();
                    var ctorConstants = _constructorArgs.Select(x => Expression.Constant(x, x.GetType())).ToArray();
                    var ctor = _type.GetConstructor(ctorTypes)
                        ?? throw new MissingMethodException("[EvokerEngine] Uyumlu Constructor bulunamadı.");
                    instance = Expression.New(ctor, ctorConstants);
                }
                // 3. Hiçbiri verilmediyse default new çalıştır
                else
                {
                    instance = Expression.New(_type);
                }
            }

            return Expression.Call(instance, methodInfo, convertedArgs);
        }

        private static readonly ConcurrentDictionary<string, Delegate> Cache = new();
    }
}
