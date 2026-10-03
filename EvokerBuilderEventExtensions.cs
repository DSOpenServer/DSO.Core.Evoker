using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace DSO.Core.Evoker
{
    /// <summary>
    /// Var olan (derlenmiş) bir tipin event'lerine, derleme zamanında tipini/delegate imzasını bilmeden
    /// abone olmak için. Event'in gerçek handler tipine (EventHandler, EventHandler&lt;T&gt;, özel delegate'ler,
    /// VB.NET'in "Event X(a As Integer)" ile ürettiği gizli delegate'ler...) uyan bir delegate, Expression
    /// Tree ile BİR KEZ derlenir; event tetiklendiğinde tüm argümanlar object[] olarak handler'a gelir.
    ///
    ///   using var sub = builder.AddEventHandler("ValueChanged", args => Console.WriteLine(args[1]));
    ///   ...                 // Dispose = abonelikten çık
    ///
    /// Static event'ler de desteklenir (instance gerekmez). Instance event'leri için builder
    /// SetInstance(...) ile bağlı olmalı. builder.IncludeNonPublic ise private event'ler de görülür.
    /// İsim önce birebir, yoksa büyük/küçük harf duyarsız aranır (VB.NET).
    /// </summary>
    public static class EvokerBuilderEventExtensions
    {
        public static IDisposable AddEventHandler(this EvokerBuilder builder, string eventName, Action<object?[]> handler)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (string.IsNullOrWhiteSpace(eventName)) throw new ArgumentException("eventName boş olamaz.", nameof(eventName));

            var evt = FindEvent(builder, eventName);
            var add = evt.GetAddMethod(nonPublic: true)!;
            var remove = evt.GetRemoveMethod(nonPublic: true)!;

            object? target = null;
            if (!add.IsStatic)
            {
                target = builder.Instance ?? throw new InvalidOperationException(
                    $"[EvokerBuilder] '{builder.Type.Name}.{evt.Name}' bir instance event'i - önce SetInstance(...) ile nesne bağlayın.");
            }

            var del = BuildForwarder(evt.EventHandlerType!, handler);
            add.Invoke(target, new object[] { del });
            return new Subscription(() => remove.Invoke(target, new object[] { del }));
        }

        /// <summary>Tipin (builder.IncludeNonPublic'e göre görülebilen) event adları.</summary>
        public static string[] GetEventNames(this EvokerBuilder builder) =>
            builder.Type.GetEvents(Flags(builder)).Select(e => e.Name).Distinct().ToArray();

        private static BindingFlags Flags(EvokerBuilder b) =>
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | (b.IncludeNonPublic ? BindingFlags.NonPublic : 0);

        private static EventInfo FindEvent(EvokerBuilder builder, string eventName)
        {
            var events = builder.Type.GetEvents(Flags(builder));
            return events.FirstOrDefault(e => e.Name == eventName)
                ?? SingleIgnoreCase(events, eventName)
                ?? throw new MissingMemberException(builder.Type.Name, eventName);
        }

        private static EventInfo? SingleIgnoreCase(EventInfo[] events, string name)
        {
            var m = events.Where(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            return m.Count == 1 ? m[0] : null;
        }

        // handlerType'ın Invoke imzasıyla birebir aynı bir lambda: (p0, p1, ...) => handler(new object[] { p0, p1, ... })
        // Dönüş tipi void değilse (nadir ama mümkün) default(dönüş) döner.
        private static Delegate BuildForwarder(Type handlerType, Action<object?[]> handler)
        {
            var invoke = handlerType.GetMethod("Invoke")
                ?? throw new InvalidOperationException($"'{handlerType}' bir delegate tipi değil.");
            var ps = invoke.GetParameters().Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();

            if (ps.Any(p => p.IsByRef))
                throw new NotSupportedException($"[EvokerBuilder] ref/out parametreli event delegate'i ({handlerType.Name}) desteklenmiyor.");

            var array = Expression.NewArrayInit(typeof(object), ps.Select(p => (Expression)Expression.Convert(p, typeof(object))));
            Expression body = Expression.Invoke(Expression.Constant(handler), array);
            if (invoke.ReturnType != typeof(void))
                body = Expression.Block(body, Expression.Default(invoke.ReturnType));

            return Expression.Lambda(handlerType, body, ps).Compile();
        }

        private sealed class Subscription : IDisposable
        {
            private Action? _remove;
            public Subscription(Action remove) => _remove = remove;
            public void Dispose() => System.Threading.Interlocked.Exchange(ref _remove, null)?.Invoke();
        }
    }
}