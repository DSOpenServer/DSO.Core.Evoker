using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;

namespace DSO.Core.Evoker
{
    /// <summary>
    /// ORM'in "satırı nesneye dönüştürme" (materialization) hot path'i için tasarlanmış,
    /// EvokerBuilder'dan BAĞIMSIZ, dar-kapsamlı bir performans katmanı.
    ///
    /// EvokerBuilder ile fark:
    ///   - EvokerBuilder genel amaçlıdır: "herhangi bir metodu string isimle, object[] args
    ///     ile çağır". Bu esneklik args array allocation + value-type boxing bedeli getirir.
    ///   - DynamicEntityAccessor DAR bir işe odaklanır: bilinen bir property'nin bilinen bir
    ///     TValue tipiyle, HİÇ boxing'siz okunup yazılması. Bunun için jenerik delegate
    ///     imzaları (Func&lt;object,TValue&gt; / Action&lt;object,TValue&gt;) kullanır - TValue
    ///     bir value type (int, decimal, DateTime...) olsa bile CLR bunu unboxed taşır.
    ///
    /// İkisi birlikte kullanılabilir: ORM'in kayıt okuma döngüsü bu sınıfı kullanırken,
    /// ad-hoc/nadiren çağrılan senaryolar (dinamik kural motoru, debug) EvokerBuilder'ı
    /// kullanmaya devam edebilir.
    /// </summary>
    public static class DynamicEntityAccessor
    {
        private static readonly ConcurrentDictionary<Type, Func<object>> ConstructorCache = new();
        private static readonly ConcurrentDictionary<string, Delegate> AccessorCache = new();
        private static readonly ConcurrentQueue<string> AccessorInsertionOrder = new();

        // İsteğe bağlı üst sınır (varsayılan sınırsız). Ad-hoc/şeması sürekli değişen
        // projeksiyonlarla çalışıyorsanız (her sorguda farklı kolon kombinasyonu) makul bir
        // değer verin (ör. 5000). Sabit/bilinen bir entity kümeniz varsa dokunmanıza gerek yok.
        // NOT: FIFO tahliyedir, gerçek LRU değildir (bkz. DynamicTypeFactory.MaxSchemaCacheSize
        // ile aynı gerekçe).
        public static int MaxAccessorCacheSize { get; set; } = int.MaxValue;

        public static int CachedConstructorCount => ConstructorCache.Count;
        public static int CachedAccessorCount => AccessorCache.Count;

        /// <summary>
        /// Parametresiz constructor'ı DERLENMİŞ bir delegate olarak döner. Activator.CreateInstance
        /// her çağrıda reflection üzerinden çözümleme yaptığı için satır-başına nesne yaratmada
        /// belirgin şekilde daha yavaştır; burada derleme maliyeti sadece İLK çağrıda ödenir.
        /// </summary>
        public static Func<object> GetConstructor(Type type)
        {
            return ConstructorCache.GetOrAdd(type, t =>
            {
                var ctor = t.GetConstructor(Type.EmptyTypes)
                    ?? throw new MissingMethodException(
                        $"[DynamicEntityAccessor] '{t.Name}' türünün parametresiz constructor'ı yok.");

                var newExpr = Expression.New(ctor);
                var lambda = Expression.Lambda<Func<object>>(Expression.Convert(newExpr, typeof(object)));
                return lambda.Compile();
            });
        }

        /// <summary>
        /// Belirtilen property için boxing'siz, tipe özel bir getter delegate'i döner (cache'li).
        /// </summary>
        public static Func<object, TValue> GetGetter<TValue>(Type type, string propertyName)
        {
            string key = BuildKey(type, "get", propertyName, typeof(TValue));

            bool added = false;
            var del = AccessorCache.GetOrAdd(key, _ =>
            {
                added = true;
                return BuildGetter<TValue>(type, propertyName);
            });

            if (added)
            {
                AccessorInsertionOrder.Enqueue(key);
                TrimAccessorCacheIfNeeded();
            }

            return (Func<object, TValue>)del;
        }

        /// <summary>
        /// Belirtilen property için boxing'siz, tipe özel bir setter delegate'i döner (cache'li).
        /// </summary>
        public static Action<object, TValue> GetSetter<TValue>(Type type, string propertyName)
        {
            string key = BuildKey(type, "set", propertyName, typeof(TValue));

            bool added = false;
            var del = AccessorCache.GetOrAdd(key, _ =>
            {
                added = true;
                return BuildSetter<TValue>(type, propertyName);
            });

            if (added)
            {
                AccessorInsertionOrder.Enqueue(key);
                TrimAccessorCacheIfNeeded();
            }

            return (Action<object, TValue>)del;
        }

        /// <summary>
        /// Uygulama açılışında (soğuk başlangıç JIT/derleme maliyetini isteğe bağlı olarak
        /// öne çekmek için) bilinen tip + property listesi için constructor ve accessor'ları
        /// önceden derler. Bilinmeyen/nadir kullanılan tipler için gerekli değildir; sadece
        /// tutarlı düşük gecikme istediğiniz, sık çağrılan tipler için kullanın.
        /// </summary>
        public static void Warmup(IEnumerable<(Type Type, IEnumerable<(string PropertyName, Type PropertyType)> Properties)> entities)
        {
            foreach (var (type, props) in entities)
            {
                GetConstructor(type);

                foreach (var (name, propType) in props)
                {
                    var getGetter = typeof(DynamicEntityAccessor)
                        .GetMethod(nameof(GetGetter))!
                        .MakeGenericMethod(propType);
                    getGetter.Invoke(null, new object[] { type, name });

                    var getSetter = typeof(DynamicEntityAccessor)
                        .GetMethod(nameof(GetSetter))!
                        .MakeGenericMethod(propType);
                    getSetter.Invoke(null, new object[] { type, name });
                }
            }
        }

        private static void TrimAccessorCacheIfNeeded()
        {
            while (AccessorCache.Count > MaxAccessorCacheSize && AccessorInsertionOrder.TryDequeue(out var oldestKey))
            {
                AccessorCache.TryRemove(oldestKey, out _);
            }
        }

        private static string BuildKey(Type type, string kind, string propertyName, Type valueType)
        {
            string typeIdentity = type.AssemblyQualifiedName ?? type.FullName ?? type.Name;
            return $"{typeIdentity}.{kind}_{propertyName}:{valueType.Name}";
        }

        private static Delegate BuildGetter<TValue>(Type type, string propertyName)
        {
            var property = type.GetProperty(propertyName)
                ?? throw new MissingMemberException(type.Name, propertyName);
            var getMethod = property.GetGetMethod()
                ?? throw new MissingMethodException(
                    $"[DynamicEntityAccessor] '{propertyName}' için erişilebilir bir get metodu yok.");

            var instanceParam = Expression.Parameter(typeof(object), "instance");
            var typedInstance = Expression.Convert(instanceParam, type);
            var call = Expression.Call(typedInstance, getMethod);

            Expression body = getMethod.ReturnType == typeof(TValue)
                ? call
                : Expression.Convert(call, typeof(TValue));

            var lambda = Expression.Lambda<Func<object, TValue>>(body, instanceParam);
            return lambda.Compile();
        }

        private static Delegate BuildSetter<TValue>(Type type, string propertyName)
        {
            var property = type.GetProperty(propertyName)
                ?? throw new MissingMemberException(type.Name, propertyName);
            var setMethod = property.GetSetMethod()
                ?? throw new MissingMethodException(
                    $"[DynamicEntityAccessor] '{propertyName}' için erişilebilir bir set metodu yok.");

            var instanceParam = Expression.Parameter(typeof(object), "instance");
            var valueParam = Expression.Parameter(typeof(TValue), "value");
            var typedInstance = Expression.Convert(instanceParam, type);

            var paramType = setMethod.GetParameters()[0].ParameterType;
            Expression valueExpr = paramType == typeof(TValue)
                ? valueParam
                : Expression.Convert(valueParam, paramType);

            var call = Expression.Call(typedInstance, setMethod, valueExpr);
            var lambda = Expression.Lambda<Action<object, TValue>>(call, instanceParam, valueParam);
            return lambda.Compile();
        }
    }
}