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

        // ÖNEMLİ: Key olarak STRING değil, value-tuple kullanılıyor. String interpolation
        // (ör. $"{type}.{kind}_{prop}:{valueType}") her çağrıda -CACHE HIT olsa bile- yeni bir
        // string allocate eder; bu da "boxing'siz" dediğimiz yolu sessizce alloc'lu hale getirir.
        // ValueTuple bir struct olduğu için burada ekstra bir heap allocation OLMAZ.
        private static readonly ConcurrentDictionary<(Type Type, string Kind, string PropertyName, Type ValueType), Delegate> AccessorCache = new();
        private static readonly ConcurrentQueue<(Type Type, string Kind, string PropertyName, Type ValueType)> AccessorInsertionOrder = new();

        // İsteğe bağlı üst sınır (varsayılan sınırsız). Ad-hoc/şeması sürekli değişen
        // projeksiyonlarla çalışıyorsanız (her sorguda farklı kolon kombinasyonu) makul bir
        // değer verin (ör. 5000). Sabit/bilinen bir entity kümeniz varsa dokunmanıza gerek yok.
        // NOT: FIFO tahliyedir, gerçek LRU değildir (bkz. DynamicTypeFactory.MaxSchemaCacheSize
        // ile aynı gerekçe).
        public static int MaxAccessorCacheSize { get; set; } = int.MaxValue;

        public static int CachedConstructorCount => ConstructorCache.Count;
        public static int CachedAccessorCount => AccessorCache.Count;

        /// <summary>
        /// Belirli bir Type'a ait TÜM cache girdilerini (constructor + o tipin tüm accessor'ları)
        /// kaldırır. Sadece GERÇEKTEN bir daha kullanılmayacak (tekrar etmeyen şema) tipler için
        /// çağırın - paylaşımlı/tekrar kullanılan bir tip için çağırırsanız, o tipi kullanan
        /// BAŞKA kod yolları da bir sonraki çağrılarında yeniden derleme bedelini öder.
        /// NOT: Type nesnesinin kendisi (CLR metadata'sı) bu çağrıyla bellekten SİLİNMEZ -
        /// Reflection.Emit ile AssemblyBuilderAccess.Run kullanılarak üretilen tipler
        /// "collectible" değildir, sadece bizim kendi dictionary cache'imizden çıkarılır.
        /// </summary>
        public static void ForgetType(Type type)
        {
            ConstructorCache.TryRemove(type, out _);

            foreach (var key in AccessorCache.Keys)
            {
                if (key.Type == type)
                {
                    AccessorCache.TryRemove(key, out _);
                }
            }
        }

        /// <summary>
        /// Parametresiz constructor'ı DERLENMİŞ bir delegate olarak döner. Activator.CreateInstance
        /// her çağrıda reflection üzerinden çözümleme yaptığı için satır-başına nesne yaratmada
        /// belirgin şekilde daha yavaştır; burada derleme maliyeti sadece İLK çağrıda ödenir.
        /// </summary>
        public static Func<object> GetConstructor(Type type)
        {
            if (ConstructorCache.TryGetValue(type, out var existing))
            {
                return existing;
            }

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
            var key = (type, "get", propertyName, typeof(TValue));

            // Önce closure/lambda ALLOCATE ETMEDEN dene (cache hit - hot path, %99 durum budur).
            if (AccessorCache.TryGetValue(key, out var existing))
            {
                return (Func<object, TValue>)existing;
            }

            // Sadece cache MISS olduğunda buraya düşüyoruz. Lambda'yı BİLEREK ayrı bir metoda
            // taşıdık: aynı metodun içinde yazılı olsaydı, C# derleyicisi capture edilen
            // 'type'/'propertyName' için closure nesnesini metot girişinde (if'ten ÖNCE)
            // allocate edebiliyordu - yani "hit path'te de" sessizce alloc oluyordu. Ayrı
            // metoda taşımak bu riski ortadan kaldırır (empirik olarak doğruladık).
            return (Func<object, TValue>)CreateAndCacheGetter<TValue>(key, type, propertyName);
        }

        private static Delegate CreateAndCacheGetter<TValue>(
            (Type Type, string Kind, string PropertyName, Type ValueType) key, Type type, string propertyName)
        {
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

            return del;
        }

        /// <summary>
        /// Belirtilen property için boxing'siz, tipe özel bir setter delegate'i döner (cache'li).
        /// </summary>
        public static Action<object, TValue> GetSetter<TValue>(Type type, string propertyName)
        {
            var key = (type, "set", propertyName, typeof(TValue));

            if (AccessorCache.TryGetValue(key, out var existing))
            {
                return (Action<object, TValue>)existing;
            }

            return (Action<object, TValue>)CreateAndCacheSetter<TValue>(key, type, propertyName);
        }

        private static Delegate CreateAndCacheSetter<TValue>(
            (Type Type, string Kind, string PropertyName, Type ValueType) key, Type type, string propertyName)
        {
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

            return del;
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

        private static Delegate BuildGetter<TValue>(Type type, string propertyName)
        {
            // NOT: DeclaredOnly ZORUNLU. DSO.Core.Evoker.Extend ile bir base class'tan türetilen
            // tiplerde (bkz. Faz 2), bizim ürettiğimiz property (ör. "Name") ile base class'ın
            // abstract "Name" property'si REFLECTION'DA aynı isimle iki ayrı PropertyInfo olarak
            // görünür (farklı DeclaringType). DeclaredOnly olmadan GetProperty(name) bu ikisi
            // arasında karar veremeyip AmbiguousMatchException fırlatır - bunu deneyerek bulduk.
            // DeclaredOnly, sadece BİZİM emit ettiğimiz (type'ın kendi üzerinde tanımlı) property'yi
            // hedefler, ki zaten her zaman doğru olan budur.
            var property = type.GetProperty(propertyName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
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
            var property = type.GetProperty(propertyName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
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