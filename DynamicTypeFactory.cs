using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Threading;

namespace DSO.Core.Evoker
{
    // --- Reflection.Emit ile Roslyn'siz Dinamik Class Üretici ---
    //
    // DEĞİŞİKLİK (performans): Eskiden CreateType() her çağrıda YENİ bir assembly + module
    // açıyordu. ORM senaryosunda düzinelerce/yüzlerce tablo şekli için bu, gereksiz assembly
    // yükleme + metadata overhead'i demekti. Artık:
    //   1) Tek bir process-ömürlü paylaşımlı ModuleBuilder kullanılıyor.
    //   2) Aynı (className, properties) şeması tekrar istendiğinde IL YENİDEN ÜRETİLMİYOR,
    //      önceden üretilmiş Type cache'ten dönüyor (schema-signature cache).
    //   3) Modülün kendisi tek bir lock ile korunuyor çünkü ModuleBuilder/TypeBuilder
    //      eşzamanlı mutasyona karşı thread-safe DEĞİL.
    public static class DynamicTypeFactory
    {
        private static readonly object ModuleLock = new();
        private static ModuleBuilder? _sharedModule;
        private static int _typeCounter;

        // Şema imzası -> üretilmiş Type. Aynı şema ikinci kez istenirse burada bulunur.
        private static readonly ConcurrentDictionary<string, Type> SchemaCache = new();

        // Type -> o Type'ın property şeması. JSON/serileştirme gibi DIŞARIDAN eklenecek
        // extension projelerinin (bkz. DSO.Core.Evoker.Json planı) DynamicTypeFactory'nin
        // ürettiği bir tipin property adı+tipi listesine ERİŞEBİLMESİ için gerekli. Core'un
        // kendisi bunu JSON/serileştirme için KULLANMAZ - sadece dışarıya açık bir sorgu noktası.
        private static readonly ConcurrentDictionary<Type, IReadOnlyList<(string Name, Type Type)>> SchemaByType = new();

        // İsteğe bağlı: şema cache'i sınırsız büyümesin diye basit bir üst sınır.
        // Varsayılan sınırsız (int.MaxValue) - mevcut davranışla birebir uyumlu.
        // NOT: Bu FIFO (ilk giren ilk çıkar) bir sınırlamadır, GERÇEK bir LRU DEĞİLDİR
        // (yakın zamanda sık kullanılan bir şema de sırası geldiğinde tahliye edilebilir).
        // Ad-hoc/sınırsız çeşitlilikte şema üreten senaryolarda bellek büyümesini durdurmak
        // için yeterlidir; sık kullanılan az sayıda şemanız varsa muhtemelen hiç dokunmanıza
        // gerek kalmaz.
        public static int MaxSchemaCacheSize { get; set; } = int.MaxValue;
        private static readonly ConcurrentQueue<string> SchemaInsertionOrder = new();

        private static ModuleBuilder GetSharedModule()
        {
            if (_sharedModule != null) return _sharedModule;
            lock (ModuleLock)
            {
                if (_sharedModule != null) return _sharedModule;
                var assemblyName = new AssemblyName("DSO.Core.Evoker.DynamicEntities");
                var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
                _sharedModule = assemblyBuilder.DefineDynamicModule("MainModule");
                return _sharedModule;
            }
        }

        /// <summary>
        /// Aynı (className, properties) şeması için CACHE'LENMİŞ tipi döner. İlk çağrıda
        /// IL üretilir, sonraki aynı-şema çağrılarında üretim tekrarlanmaz.
        /// ÖNEMLİ DAVRANIŞ DEĞİŞİKLİĞİ: Eski sürümde her çağrı YENİ bir Type döndürüyordu.
        /// Artık aynı şema aynı Type nesnesini döner. Kesinlikle izole/tekil bir tip
        /// istiyorsanız (ör. çoklu-kiracı izolasyonu, testler) CreateUniqueType() kullanın.
        /// </summary>
        /// <param name="configureType">
        /// GENİŞLETME NOKTASI (opsiyonel, varsayılan null - mevcut çağrılar hiç etkilenmez).
        /// Property emisyonu bittikten, ama typeBuilder.CreateType() ile tip "kilitlenmeden"
        /// HEMEN ÖNCE çağrılır. Interface implementasyonu, base class ayarı gibi ek IL
        /// işlemleri için (bkz. DSO.Core.Evoker.Extend planı - madde 3/v2) kullanılabilir.
        /// ÖNEMLİ: configureType verildiğinde şema cache'i BYPASS EDİLİR (CreateUniqueType gibi
        /// davranır) - çünkü iki farklı configureType çağrısı AYNI (className,properties) ile
        /// FARKLI tipler üretebilir (biri interface implement eder, diğeri etmez) ve şema cache'i
        /// sadece property setine baktığı için bu farkı ayırt edemez. Kendi caching stratejinizi
        /// (ör. configureType'ın kimliğini de içeren bir anahtarla) kendi katmanınızda kurun.
        /// </param>
        public static Type CreateType(string className, Dictionary<string, Type> properties, Action<TypeBuilder, IReadOnlyDictionary<string, (MethodBuilder Get, MethodBuilder Set)>>? configureType = null)
        {
            if (configureType != null)
            {
                return EmitType(className, properties, configureType);
            }

            string signature = BuildSignature(className, properties);

            bool added = false;
            Type type = SchemaCache.GetOrAdd(signature, _ =>
            {
                added = true;
                return EmitType(className, properties, null);
            });

            if (added)
            {
                SchemaInsertionOrder.Enqueue(signature);
                TrimSchemaCacheIfNeeded();
            }

            return type;

            // NOT: ConcurrentDictionary.GetOrAdd'ın valueFactory'si teorik olarak birden
            // fazla thread'de eşzamanlı tetiklenip birden fazla Type üretebilir (biri
            // atılır) - ama EmitType içindeki modül mutasyonu ayrı bir lock ile korunduğu
            // için bu durumda bile YARIM/BOZUK bir tip asla cache'e girmez, sadece nadiren
            // fazladan (kullanılmayan) bir tip üretilip çöpe gider.
        }

        /// <summary>
        /// Şema cache'ini BYPASS EDER: her çağrıda kesinlikle yeni, izole bir Type üretir.
        /// Eski CreateType() davranışının karşılığı budur.
        /// </summary>
        public static Type CreateUniqueType(string className, Dictionary<string, Type> properties, Action<TypeBuilder, IReadOnlyDictionary<string, (MethodBuilder Get, MethodBuilder Set)>>? configureType = null)
        {
            return EmitType(className, properties, configureType);
        }

        /// <summary>
        /// Belirli bir şemayı şema cache'inden çıkarır (Type nesnesinin kendisini silmez,
        /// sadece "bu şema tekrar istenirse yeniden üretilsin" der). Nadiren gereken bir
        /// bakım operasyonu - bkz. DynamicClass.Dispose(forgetOnDispose:true).
        /// </summary>
        public static bool ForgetSchema(string className, Dictionary<string, Type> properties)
        {
            string signature = BuildSignature(className, properties);
            return SchemaCache.TryRemove(signature, out _);
        }

        private static void TrimSchemaCacheIfNeeded()
        {
            while (SchemaCache.Count > MaxSchemaCacheSize && SchemaInsertionOrder.TryDequeue(out var oldestKey))
            {
                SchemaCache.TryRemove(oldestKey, out _);
                // NOT: Bu tahliye SADECE şema->Type eşlemesini cache'ten çıkarır. O Type
                // nesnesine göre DynamicEntityAccessor'da önceden derlenmiş constructor/
                // getter/setter delegate'leri varsa onlar etkilenmez (Type hâlâ bellekte
                // yaşar, sadece "bu şemayı tekrar istersen yeni bir Type üretilir" anlamına
                // gelir). Bu, çakışma değil, sadece cache'in kendi sorumluluk alanının sınırı.
            }
        }

        private static string BuildSignature(string className, Dictionary<string, Type> properties)
        {
            // Dictionary'nin enumeration sırası garanti değildir; aynı şema farklı sırayla
            // verildiğinde cache miss oluşmaması için isimlere göre sıralıyoruz.
            var sb = new StringBuilder(className);
            foreach (var kvp in properties.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                sb.Append('|').Append(kvp.Key).Append(':').Append(kvp.Value.AssemblyQualifiedName);
            }
            return sb.ToString();
        }

        private static Type EmitType(string className, Dictionary<string, Type> properties, Action<TypeBuilder, IReadOnlyDictionary<string, (MethodBuilder Get, MethodBuilder Set)>>? configureType)
        {
            lock (ModuleLock)
            {
                var moduleBuilder = GetSharedModule();

                // Modül içinde tip adları TEKİL olmak zorunda. Aynı className farklı
                // şemalarla (veya CreateUniqueType ile tekrar tekrar) istenebileceği için
                // görünen isme bir sayaç ekleniyor. Type.Name bu yüzden artık "Customer"
                // değil "Customer_7" gibi görünür - işlevsel hiçbir şeyi etkilemez (kod
                // hiçbir yerde Type.Name'e göre string eşleştirme yapmıyor), sadece debug
                // çıktısında/ToString()'de fark edilir.
                int id = Interlocked.Increment(ref _typeCounter);
                string internalName = $"{className}_{id}";

                var typeBuilder = moduleBuilder.DefineType(internalName, TypeAttributes.Public | TypeAttributes.Class);
                var methodBuildersByProperty = new Dictionary<string, (MethodBuilder Get, MethodBuilder Set)>();

                foreach (var prop in properties)
                {
                    string propName = prop.Key;
                    Type propType = prop.Value;

                    // NOT (Virtual|NewSlot): metodlar BİLEREK virtual üretiliyor. Sebep: bir
                    // interface implementasyonu (configureType ile DefineMethodOverride) CLR
                    // tarafında SADECE virtual metodlarla mümkün ("must be virtual to implement
                    // a method on an interface" - bunu deneyerek bulduk). Sıradan (interface'siz)
                    // kullanımda bu HİÇBİR DAVRANIŞ FARKI yaratmaz - çağrılar zaten somut Type
                    // üzerinden (Expression.Call ile) yapılıyor, virtual dispatch'in ekstra
                    // maliyeti (bir vtable slotu) ölçülemeyecek kadar küçük.
                    FieldBuilder fieldBuilder = typeBuilder.DefineField($"_{propName.ToLower()}", propType, FieldAttributes.Private);
                    PropertyBuilder propertyBuilder = typeBuilder.DefineProperty(propName, PropertyAttributes.HasDefault, propType, null);

                    // Getter: public T get_PropName() => _propName;
                    MethodBuilder getMethodBuilder = typeBuilder.DefineMethod(
                        $"get_{propName}",
                        MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig | MethodAttributes.Virtual | MethodAttributes.NewSlot,
                        propType,
                        Type.EmptyTypes);

                    ILGenerator getIL = getMethodBuilder.GetILGenerator();
                    getIL.Emit(OpCodes.Ldarg_0);
                    getIL.Emit(OpCodes.Ldfld, fieldBuilder);
                    getIL.Emit(OpCodes.Ret);

                    // Setter: public void set_PropName(T value) => _propName = value;
                    MethodBuilder setMethodBuilder = typeBuilder.DefineMethod(
                        $"set_{propName}",
                        MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig | MethodAttributes.Virtual | MethodAttributes.NewSlot,
                        null,
                        new[] { propType });

                    ILGenerator setIL = setMethodBuilder.GetILGenerator();
                    setIL.Emit(OpCodes.Ldarg_0);
                    setIL.Emit(OpCodes.Ldarg_1);
                    setIL.Emit(OpCodes.Stfld, fieldBuilder);
                    setIL.Emit(OpCodes.Ret);

                    propertyBuilder.SetGetMethod(getMethodBuilder);
                    propertyBuilder.SetSetMethod(setMethodBuilder);

                    methodBuildersByProperty[propName] = (getMethodBuilder, setMethodBuilder);
                }

                // GENİŞLETME NOKTASI: property emisyonu bitti, tip henüz "kilitlenmedi"
                // (CreateType() çağrılmadı). configureType'a TypeBuilder + zaten ürettiğimiz
                // get/set MethodBuilder'larını (property adına göre) veriyoruz - çünkü
                // TypeBuilder.GetMethod() tip CreateType() ile tamamlanmadan ÇALIŞMIYOR
                // (NotSupportedException), tek yol bu referansları BAŞTAN elden ele geçirmek.
                // Örn. bir interface property'sini implement etmek için:
                //   tb.DefineMethodOverride(methodBuilders["Id"].Get, ifaceType.GetMethod("get_Id"));
                configureType?.Invoke(typeBuilder, methodBuildersByProperty);

                Type createdType = typeBuilder.CreateType()!;
                SchemaByType[createdType] = properties.Select(p => (p.Key, p.Value)).ToList();
                return createdType;
            }
        }

        /// <summary>
        /// Bu Type, DynamicTypeFactory tarafından mı üretildi? (CreateType veya CreateUniqueType ile)
        /// Örn. bir JsonConverterFactory'nin "bu tipi ben mi ürettim, özel mi davranayım" kararı
        /// vermesi için kullanılabilir.
        /// </summary>
        public static bool IsDynamicType(Type type) => SchemaByType.ContainsKey(type);

        /// <summary>
        /// Bu Type DynamicTypeFactory tarafından üretildiyse property (ad, tip) listesini döner,
        /// üretilmediyse null döner. Sıra, AddProperty/Dictionary ile verilme sırasıyla AYNI
        /// değildir (Dictionary enumeration garantisi yoktur) - isimle eşleştirin, sırayla değil.
        /// </summary>
        public static IReadOnlyList<(string Name, Type Type)>? GetSchema(Type type)
            => SchemaByType.TryGetValue(type, out var schema) ? schema : null;
    }
}