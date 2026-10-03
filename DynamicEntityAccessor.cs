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
        // Key'e includeNonPublic de dahil: aynı tip için hem "sadece public ctor" hem "private ctor
        // dahil" arama farklı sonuçlar üretebilir (ör. tip SADECE private parametresiz ctor'a sahipse),
        // ikisi aynı cache girdisini paylaşırsa biri diğerini ezer.
        private static readonly ConcurrentDictionary<(Type Type, bool IncludeNonPublic), Func<object>> ConstructorCache = new();

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
            foreach (var key in ConstructorCache.Keys)
            {
                if (key.Type == type)
                {
                    ConstructorCache.TryRemove(key, out _);
                }
            }

            foreach (var key in AccessorCache.Keys)
            {
                if (key.Type == type || key.ValueType == type)
                {
                    AccessorCache.TryRemove(key, out _);
                }
            }

            // FIFO tahliye kuyruğu da anahtarları (dolayısıyla Type'ı) tutuyor - sadece cache'ten silmek
            // YETMEZ: kuyrukta kalan anahtar Type'a güçlü referans olarak kalır ve (plugin senaryosunda)
            // tipin AssemblyLoadContext'inin unload edilmesini ENGELLER. Kuyruk bu tip hariç yeniden kurulur.
            // (ForgetType nadir çağrılır - kuyruğu baştan kurmanın maliyeti önemsiz.)
            lock (InsertionOrderRebuildLock)
            {
                int count = AccessorInsertionOrder.Count;
                for (int i = 0; i < count && AccessorInsertionOrder.TryDequeue(out var k); i++)
                {
                    if (k.Type != type && k.ValueType != type)
                        AccessorInsertionOrder.Enqueue(k);
                }
            }
        }

        private static readonly object InsertionOrderRebuildLock = new();

        /// <summary>
        /// Parametresiz constructor'ı DERLENMİŞ bir delegate olarak döner. Activator.CreateInstance
        /// her çağrıda reflection üzerinden çözümleme yaptığı için satır-başına nesne yaratmada
        /// belirgin şekilde daha yavaştır; burada derleme maliyeti sadece İLK çağrıda ödenir.
        /// <paramref name="includeNonPublic"/> true ise private/protected parametresiz constructor'lar
        /// da bulunur - Type.GetConstructor(Type.EmptyTypes) (parametresiz overload) SADECE public
        /// instance ctor arar, plugin tipinin tek ctor'u private/protected ise bunu asla bulamaz.
        /// </summary>
        public static Func<object> GetConstructor(Type type, bool includeNonPublic = false)
        {
            var key = (type, includeNonPublic);
            if (ConstructorCache.TryGetValue(key, out var existing))
            {
                return existing;
            }

            return ConstructorCache.GetOrAdd(key, k =>
            {
                var flags = BindingFlags.Instance | BindingFlags.Public
                    | (k.IncludeNonPublic ? BindingFlags.NonPublic : 0);
                var ctor = k.Type.GetConstructor(flags, binder: null, Type.EmptyTypes, modifiers: null)
                    ?? throw new MissingMethodException(
                        $"[DynamicEntityAccessor] '{k.Type.Name}' türünün{(k.IncludeNonPublic ? "" : " (public)")} parametresiz constructor'ı yok.");

                var newExpr = Expression.New(ctor);
                var lambda = Expression.Lambda<Func<object>>(Expression.Convert(newExpr, typeof(object)));
                return lambda.Compile();
            });
        }

        /// <summary>
        /// Belirtilen property (ya da property yoksa aynı isimli alan/field - fallback) için
        /// boxing'siz, tipe özel bir getter delegate'i döner (cache'li).
        /// <paramref name="includeNonPublic"/> true ise private/protected/internal property ve
        /// field'lar da aranır - plugin senaryosunda 3. parti bir DLL'in dışarı açmadığı bir
        /// alanı/property'yi (ör. admin/tanılama amaçlı) okumak için. Varsayılan false: eskisi gibi
        /// sadece public üyeler görünür - mevcut çağıran kodlar davranış DEĞİŞTİRMEDEN çalışmaya
        /// devam eder.
        /// </summary>
        public static Func<object, TValue> GetGetter<TValue>(Type type, string propertyName, bool includeNonPublic = false)
        {
            // NOT: Kind string'ine "-np" eklenerek public/non-public aramalar AYRI cache girdileri
            // olarak tutuluyor - key tuple'ının şeklini (Type/Kind/PropertyName/ValueType) değiştirmeden.
            // Aksi halde aynı (Type, PropertyName) için ÖNCE public modda arayıp bulunamadıysa, SONRA
            // non-public modda arayan bir çağıran, ikinci çağrıda yanlışlıkla ilk (başarısız olmuş
            // olabilecek) sonucu cache'ten görebilirdi.
            string kind = includeNonPublic ? "get-np" : "get";
            var key = (type, kind, propertyName, typeof(TValue));

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
            return (Func<object, TValue>)CreateAndCacheGetter<TValue>(key, type, propertyName, includeNonPublic);
        }

        private static Delegate CreateAndCacheGetter<TValue>(
            (Type Type, string Kind, string PropertyName, Type ValueType) key, Type type, string propertyName, bool includeNonPublic)
        {
            bool added = false;
            var del = AccessorCache.GetOrAdd(key, _ =>
            {
                added = true;
                return BuildGetter<TValue>(type, propertyName, includeNonPublic);
            });

            if (added)
            {
                AccessorInsertionOrder.Enqueue(key);
                TrimAccessorCacheIfNeeded();
            }

            return del;
        }

        /// <summary>
        /// Belirtilen property (ya da property yoksa aynı isimli alan/field - fallback, readonly/const
        /// hariç) için boxing'siz, tipe özel bir setter delegate'i döner (cache'li).
        /// <paramref name="includeNonPublic"/> - bkz. <see cref="GetGetter{TValue}"/>.
        /// </summary>
        public static Action<object, TValue> GetSetter<TValue>(Type type, string propertyName, bool includeNonPublic = false)
        {
            string kind = includeNonPublic ? "set-np" : "set";
            var key = (type, kind, propertyName, typeof(TValue));

            if (AccessorCache.TryGetValue(key, out var existing))
            {
                return (Action<object, TValue>)existing;
            }

            return (Action<object, TValue>)CreateAndCacheSetter<TValue>(key, type, propertyName, includeNonPublic);
        }

        private static Delegate CreateAndCacheSetter<TValue>(
            (Type Type, string Kind, string PropertyName, Type ValueType) key, Type type, string propertyName, bool includeNonPublic)
        {
            bool added = false;
            var del = AccessorCache.GetOrAdd(key, _ =>
            {
                added = true;
                return BuildSetter<TValue>(type, propertyName, includeNonPublic);
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
                    // NOT: MethodInfo.Invoke, eksik argüman için OTOMATİK olarak optional parametrenin
                    // varsayılanını uygulamaz (bu sadece C# derleyicisinin bir kolaylığıdır) - GetGetter/
                    // GetSetter'ın 3. (includeNonPublic) parametresi burada AÇIKÇA geçilmezse
                    // TargetParameterCountException fırlatır. Warmup her zaman public erişimi ısıtır.
                    var getGetter = typeof(DynamicEntityAccessor)
                        .GetMethod(nameof(GetGetter))!
                        .MakeGenericMethod(propType);
                    getGetter.Invoke(null, new object[] { type, name, false });

                    var getSetter = typeof(DynamicEntityAccessor)
                        .GetMethod(nameof(GetSetter))!
                        .MakeGenericMethod(propType);
                    getSetter.Invoke(null, new object[] { type, name, false });
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

        // Instance + DeclaredOnly HER ZAMAN sabit (bkz. yorum aşağıda - base class çakışması).
        // Public/NonPublic ise includeNonPublic parametresine göre RUNTIME'da ekleniyor.
        private const System.Reflection.BindingFlags BaseMemberFlags =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly;

        private static System.Reflection.BindingFlags MemberFlags(bool includeNonPublic) =>
            BaseMemberFlags | System.Reflection.BindingFlags.Public
            | (includeNonPublic ? System.Reflection.BindingFlags.NonPublic : 0);

        // VB.NET case-insensitive: birebir isim bulunamazsa büyük/küçük harf duyarsız TEK bir eşleşme
        // aranır. Birden fazla eşleşme varsa (C#'ta "Name" ve "NAME" ayrı üyeler olabilir) belirsizliği
        // tahminle çözmüyoruz - bulunamadı sayılır (birebir isim zaten önce denendi).
        private static T? FindIgnoreCase<T>(T[] members, string name) where T : MemberInfo
        {
            T? found = null;
            foreach (var m in members)
            {
                if (!string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (found != null) return null;
                found = m;
            }
            return found;
        }

        private static Delegate BuildGetter<TValue>(Type type, string propertyName, bool includeNonPublic = false)
        {
            // NOT: DeclaredOnly ZORUNLU. DSO.Core.Evoker.Extend ile bir base class'tan türetilen
            // tiplerde (bkz. Faz 2), bizim ürettiğimiz property (ör. "Name") ile base class'ın
            // abstract "Name" property'si REFLECTION'DA aynı isimle iki ayrı PropertyInfo olarak
            // görünür (farklı DeclaringType). DeclaredOnly olmadan GetProperty(name) bu ikisi
            // arasında karar veremeyip AmbiguousMatchException fırlatır - bunu deneyerek bulduk.
            // DeclaredOnly, sadece BİZİM emit ettiğimiz (type'ın kendi üzerinde tanımlı) property'yi
            // hedefler, ki zaten her zaman doğru olan budur.
            var flags = MemberFlags(includeNonPublic);
            var property = type.GetProperty(propertyName, flags) ?? FindIgnoreCase(type.GetProperties(flags), propertyName);
            if (property != null)
            {
                // includeNonPublic=true iken GetGetMethod(true) çağrılıyor - property PUBLIC olsa bile
                // get'i private olabilir (ör. "public string Name { get; private set; }" - Set için
                // NonPublic gerekir ama Get zaten public'tir; tersi de mümkün: property NonPublic
                // arandığı için bulunduysa get metodu da genelde NonPublic'tir). GetGetMethod(true)
                // her iki durumda da "var olan en erişilebilir get'i" değil, GERÇEK get metodunu döner.
                var getMethod = property.GetGetMethod(includeNonPublic)
                    ?? throw new MissingMethodException(
                        $"[DynamicEntityAccessor] '{propertyName}' için erişilebilir bir get metodu yok.");

                var instanceParamP = Expression.Parameter(typeof(object), "instance");
                var typedInstanceP = Expression.Convert(instanceParamP, type);
                var call = Expression.Call(typedInstanceP, getMethod);

                Expression bodyP = getMethod.ReturnType == typeof(TValue)
                    ? call
                    : Expression.Convert(call, typeof(TValue));

                return Expression.Lambda<Func<object, TValue>>(bodyP, instanceParamP).Compile();
            }

            // Property bulunamadı - PLUGIN senaryosunda (3. parti DLL, arayüz zorunluluğu yok)
            // bir alan (field) her zaman bir property olmayabilir; VB.NET veya hızlıca yazılmış
            // C# kodunda düz "public/private int X;" gibi ALAN'lar oldukça yaygın. DynamicEntityAccessor
            // başlangıçta sadece ORM materialization (hep property'li POCO'lar) için tasarlandığından
            // bu yol yoktu - burada FALLBACK olarak ekleniyor.
            var field = type.GetField(propertyName, flags) ?? FindIgnoreCase(type.GetFields(flags), propertyName);
            if (field != null)
            {
                var instanceParamF = Expression.Parameter(typeof(object), "instance");
                var typedInstanceF = Expression.Convert(instanceParamF, type);
                var fieldAccess = Expression.Field(typedInstanceF, field);

                Expression bodyF = field.FieldType == typeof(TValue)
                    ? fieldAccess
                    : Expression.Convert(fieldAccess, typeof(TValue));

                return Expression.Lambda<Func<object, TValue>>(bodyF, instanceParamF).Compile();
            }

            throw new MissingMemberException(type.Name, propertyName);
        }

        private static Delegate BuildSetter<TValue>(Type type, string propertyName, bool includeNonPublic = false)
        {
            var flags = MemberFlags(includeNonPublic);
            var property = type.GetProperty(propertyName, flags) ?? FindIgnoreCase(type.GetProperties(flags), propertyName);
            if (property != null)
            {
                var setMethod = property.GetSetMethod(includeNonPublic)
                    ?? throw new MissingMethodException(
                        $"[DynamicEntityAccessor] '{propertyName}' için erişilebilir bir set metodu yok.");

                var instanceParamP = Expression.Parameter(typeof(object), "instance");
                var valueParamP = Expression.Parameter(typeof(TValue), "value");
                var typedInstanceP = Expression.Convert(instanceParamP, type);

                var paramType = setMethod.GetParameters()[0].ParameterType;
                Expression valueExprP = paramType == typeof(TValue)
                    ? valueParamP
                    : Expression.Convert(valueParamP, paramType);

                var call = Expression.Call(typedInstanceP, setMethod, valueExprP);
                return Expression.Lambda<Action<object, TValue>>(call, instanceParamP, valueParamP).Compile();
            }

            // Bkz. BuildGetter'daki aynı gerekçe: property yoksa alan (field) fallback'i.
            var field = type.GetField(propertyName, flags) ?? FindIgnoreCase(type.GetFields(flags), propertyName);
            if (field != null)
            {
                if (field.IsInitOnly || field.IsLiteral)
                    throw new MissingMethodException(
                        $"[DynamicEntityAccessor] '{propertyName}' alanı salt-okunur (readonly/const) - yazılamaz.");

                var instanceParamF = Expression.Parameter(typeof(object), "instance");
                var valueParamF = Expression.Parameter(typeof(TValue), "value");
                var typedInstanceF = Expression.Convert(instanceParamF, type);
                var fieldAccess = Expression.Field(typedInstanceF, field);

                Expression valueExprF = field.FieldType == typeof(TValue)
                    ? valueParamF
                    : Expression.Convert(valueParamF, field.FieldType);

                var assign = Expression.Assign(fieldAccess, valueExprF);
                return Expression.Lambda<Action<object, TValue>>(assign, instanceParamF, valueParamF).Compile();
            }

            throw new MissingMemberException(type.Name, propertyName);
        }
    }
}