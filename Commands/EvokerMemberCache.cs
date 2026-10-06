using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;

namespace DSO.Core.Evoker.Commands
{
    /// <summary>
    /// Komut çalıştırıcının derlenmiş erişimcileri: belirli bir MethodBase için invoker, property/field için getter/setter,
    /// Task&lt;T&gt; / ValueTask sonuç okuyucu ve isimle üye aramalarının cache'i. Hepsi tiplere güçlü referans tutar -
    /// plugin unload'u için EvokerBuilder.ForgetType bunu da temizler.
    /// </summary>
    internal static class EvokerMemberCache
    {
        private static readonly ConcurrentDictionary<MethodBase, Func<object?, object?[], object?>> MethodInvokers = new();
        private static readonly ConcurrentDictionary<MemberInfo, Func<object?, object?>> Getters = new();
        private static readonly ConcurrentDictionary<MemberInfo, Action<object?, object?>> Setters = new();
        private static readonly ConcurrentDictionary<Type, Func<object, object?>> AwaitableReaders = new(); // Task<T>/ValueTask<T> -> Task<T>
        private static readonly ConcurrentDictionary<Type, Func<object, object?>> ResultReaders = new();    // Task<T> -> Result
        private static readonly ConcurrentDictionary<(Type Type, string Name, bool NonPublic), MethodInfo[]> MethodLookups = new();
        private static readonly ConcurrentDictionary<(Type Type, string Name, bool NonPublic), MemberInfo?> DataLookups = new();

        public static BindingFlags Flags(bool nonPublic) =>
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.FlattenHierarchy | (nonPublic ? BindingFlags.NonPublic : 0);

        /// <summary>İsimle metotlar: önce birebir ad, yoksa büyük/küçük harf duyarsız (VB.NET). System.Object metotları hariç.</summary>
        public static MethodInfo[] Methods(Type type, string name, bool nonPublic) =>
            MethodLookups.GetOrAdd((type, name, nonPublic), k =>
            {
                var all = k.Type.GetMethods(Flags(k.NonPublic)).Where(m => !m.IsSpecialName && m.DeclaringType != typeof(object)).ToArray();
                var exact = all.Where(m => m.Name == k.Name).ToArray();
                return exact.Length > 0 ? exact : all.Where(m => string.Equals(m.Name, k.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            });

        /// <summary>İsimle property ya da field (önce property). Birebir ad, yoksa TEK büyük/küçük harf duyarsız eşleşme.</summary>
        public static MemberInfo? DataMember(Type type, string name, bool nonPublic) =>
            DataLookups.GetOrAdd((type, name, nonPublic), k =>
            {
                var f = Flags(k.NonPublic);
                var props = k.Type.GetProperties(f).Where(p => p.GetIndexParameters().Length == 0).ToArray();
                var fields = k.Type.GetFields(f).Where(x => !x.Name.StartsWith("<")).ToArray();
                MemberInfo? m = props.FirstOrDefault(p => p.Name == k.Name) ?? (MemberInfo?)fields.FirstOrDefault(x => x.Name == k.Name);
                if (m != null) return m;
                var ci = props.Where(p => string.Equals(p.Name, k.Name, StringComparison.OrdinalIgnoreCase)).Cast<MemberInfo>()
                    .Concat(fields.Where(x => string.Equals(x.Name, k.Name, StringComparison.OrdinalIgnoreCase))).ToList();
                return ci.Count == 1 ? ci[0] : null;
            });

        public static Type DataType(MemberInfo m) => m is PropertyInfo p ? p.PropertyType : ((FieldInfo)m).FieldType;
        public static bool IsStatic(MemberInfo m) => m is PropertyInfo p ? (p.GetGetMethod(true) ?? p.GetSetMethod(true))!.IsStatic : ((FieldInfo)m).IsStatic;

        /// <summary>Tüm parametreleri (eksiksiz) alan derlenmiş çağrı. Constructor için instance yok sayılır ve yeni nesne döner.</summary>
        public static Func<object?, object?[], object?> Invoker(MethodBase method) => MethodInvokers.GetOrAdd(method, BuildInvoker);

        private static Func<object?, object?[], object?> BuildInvoker(MethodBase method)
        {
            var inst = Expression.Parameter(typeof(object), "instance");
            var args = Expression.Parameter(typeof(object?[]), "args");
            var ps = method.GetParameters();
            var callArgs = ps.Select((p, i) => (Expression)Expression.Convert(Expression.ArrayIndex(args, Expression.Constant(i)), p.ParameterType)).ToArray();

            Expression body;
            if (method is ConstructorInfo ctor)
            {
                body = Expression.Convert(Expression.New(ctor, callArgs), typeof(object));
            }
            else
            {
                var mi = (MethodInfo)method;
                Expression? target = mi.IsStatic ? null : Expression.Convert(inst, mi.DeclaringType!);
                var call = Expression.Call(target, mi, callArgs);
                body = mi.ReturnType == typeof(void)
                    ? Expression.Block(call, Expression.Constant(null, typeof(object)))
                    : Expression.Convert(call, typeof(object));
            }
            return Expression.Lambda<Func<object?, object?[], object?>>(body, inst, args).Compile();
        }

        public static Func<object?, object?> Getter(MemberInfo member) => Getters.GetOrAdd(member, m =>
        {
            var inst = Expression.Parameter(typeof(object), "instance");
            Expression access;
            if (m is PropertyInfo p)
            {
                var get = p.GetGetMethod(true) ?? throw new EvokerCommandException(EvokerErrorCodes.InvalidOperation, $"'{p.Name}' property'sinin get erişimi yok.");
                access = Expression.Call(get.IsStatic ? null : Expression.Convert(inst, p.DeclaringType!), get);
            }
            else
            {
                var f = (FieldInfo)m;
                if (f.IsLiteral) { var c = f.GetRawConstantValue(); return _ => c; }
                access = Expression.Field(f.IsStatic ? null : Expression.Convert(inst, f.DeclaringType!), f);
            }
            return Expression.Lambda<Func<object?, object?>>(Expression.Convert(access, typeof(object)), inst).Compile();
        });

        public static Action<object?, object?> Setter(MemberInfo member) => Setters.GetOrAdd(member, m =>
        {
            var inst = Expression.Parameter(typeof(object), "instance");
            var value = Expression.Parameter(typeof(object), "value");
            if (m is PropertyInfo p)
            {
                var set = p.GetSetMethod(true) ?? throw new EvokerCommandException(EvokerErrorCodes.InvalidOperation, $"'{p.Name}' property'si salt okunur (set yok).");
                var call = Expression.Call(set.IsStatic ? null : Expression.Convert(inst, p.DeclaringType!), set, Expression.Convert(value, p.PropertyType));
                return Expression.Lambda<Action<object?, object?>>(call, inst, value).Compile();
            }
            var f = (FieldInfo)m;
            if (f.IsLiteral) throw new EvokerCommandException(EvokerErrorCodes.InvalidOperation, $"'{f.Name}' bir sabit (const) - yazılamaz.");
            if (f.IsInitOnly) throw new EvokerCommandException(EvokerErrorCodes.InvalidOperation, $"'{f.Name}' readonly field - yazılamaz.");
            var assign = Expression.Assign(Expression.Field(f.IsStatic ? null : Expression.Convert(inst, f.DeclaringType!), f), Expression.Convert(value, f.FieldType));
            return Expression.Lambda<Action<object?, object?>>(assign, inst, value).Compile();
        });

        /// <summary>
        /// Metodun DEKLARE edilmiş dönüş tipine göre sonucu bekler: Task → null, Task&lt;T&gt; → T, ValueTask(&lt;T&gt;) → aynısı;
        /// diğerleri olduğu gibi. (Çalışma anındaki tipe değil deklare tipe bakılır: "async Task" metodu runtime'da
        /// Task&lt;VoidTaskResult&gt; döner.)
        /// </summary>
        public static async Task<object?> AwaitResultAsync(object? raw, Type declaredReturnType)
        {
            if (raw == null) return null;
            if (declaredReturnType == typeof(Task)) { await ((Task)raw).ConfigureAwait(false); return null; }
            if (declaredReturnType == typeof(ValueTask)) { await ((ValueTask)raw).ConfigureAwait(false); return null; }
            if (declaredReturnType.IsGenericType)
            {
                var def = declaredReturnType.GetGenericTypeDefinition();
                if (def == typeof(Task<>) || def == typeof(ValueTask<>))
                {
                    var reader = AwaitableReaders.GetOrAdd(declaredReturnType, BuildReader);
                    var task = (Task)reader(raw)!; // Task<T> (ValueTask<T> için AsTask())
                    await task.ConfigureAwait(false);
                    return ResultReaders.GetOrAdd(typeof(Task<>).MakeGenericType(declaredReturnType.GetGenericArguments()[0]), BuildResultReader)(task);
                }
            }
            return raw;
        }

        // Task<T> -> kendisi; ValueTask<T> -> AsTask()
        private static Func<object, object?> BuildReader(Type t)
        {
            var p = Expression.Parameter(typeof(object), "o");
            Expression body = t.GetGenericTypeDefinition() == typeof(ValueTask<>)
                ? Expression.Call(Expression.Convert(p, t), t.GetMethod(nameof(ValueTask<int>.AsTask))!)
                : Expression.Convert(p, t);
            return Expression.Lambda<Func<object, object?>>(Expression.Convert(body, typeof(object)), p).Compile();
        }

        // Task<T> -> Result
        private static Func<object, object?> BuildResultReader(Type taskOfT)
        {
            var p = Expression.Parameter(typeof(object), "o");
            var result = Expression.Property(Expression.Convert(p, taskOfT), "Result");
            return Expression.Lambda<Func<object, object?>>(Expression.Convert(result, typeof(object)), p).Compile();
        }

        /// <summary>Bu tipe dokunan tüm girdileri bırakır (plugin unload).</summary>
        public static void ForgetType(Type type)
        {
            foreach (var k in MethodInvokers.Keys) if (Touches(k, type)) MethodInvokers.TryRemove(k, out _);
            foreach (var k in Getters.Keys) if (Touches(k, type)) Getters.TryRemove(k, out _);
            foreach (var k in Setters.Keys) if (Touches(k, type)) Setters.TryRemove(k, out _);
            foreach (var k in AwaitableReaders.Keys) if (Involves(k, type)) AwaitableReaders.TryRemove(k, out _);
            foreach (var k in ResultReaders.Keys) if (Involves(k, type)) ResultReaders.TryRemove(k, out _);
            foreach (var k in MethodLookups.Keys) if (k.Type == type) MethodLookups.TryRemove(k, out _);
            foreach (var k in DataLookups.Keys) if (k.Type == type) DataLookups.TryRemove(k, out _);
        }

        private static bool Touches(MemberInfo m, Type type)
        {
            if (m.DeclaringType == type || m.ReflectedType == type) return true;
            if (m is MethodBase mb)
            {
                if (mb is MethodInfo mi && Involves(mi.ReturnType, type)) return true;
                return mb.GetParameters().Any(p => Involves(p.ParameterType, type));
            }
            return Involves(DataType(m), type);
        }

        private static bool Involves(Type t, Type target) =>
            t == target
            || (t.HasElementType && Involves(t.GetElementType()!, target))
            || (t.IsGenericType && !t.IsGenericTypeDefinition && t.GetGenericArguments().Any(g => Involves(g, target)));
    }
}