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
    //   2) Aynı (className, properties, methods) şeması tekrar istendiğinde IL YENİDEN
    //      ÜRETİLMİYOR, önceden üretilmiş Type cache'ten dönüyor (schema-signature cache).
    //   3) Modülün kendisi tek bir lock ile korunuyor çünkü ModuleBuilder/TypeBuilder
    //      eşzamanlı mutasyona karşı thread-safe DEĞİL.
    //
    // FAZ 3 EKLEMESİ: Property'lerin yanında artık METOT da üretilebiliyor. Bir metot,
    // gerçek bir davranış GÖVDESİ DEĞİL - çağıranın sonradan (DynamicClass.SetMethod ile)
    // atayacağı bir DELEGATE'e yönlendiren ince bir "forwarder"dır. Somut tip, özel bir alanda
    // (field) o delegate'i tutar; üretilen metot çağrıldığında o delegate'i invoke eder.
    // Bu, Castle DynamicProxy'nin interceptor mantığının çok basitleştirilmiş bir versiyonu.
    public static class DynamicTypeFactory
    {
        private static readonly object ModuleLock = new();
        private static ModuleBuilder? _sharedModule;
        private static int _typeCounter;

        // Şema imzası -> üretilmiş Type. Aynı şema ikinci kez istenirse burada bulunur.
        private static readonly ConcurrentDictionary<string, Type> SchemaCache = new();

        // Type -> o Type'ın property şeması. JSON/serileştirme gibi DIŞARIDAN eklenecek
        // extension projelerinin DynamicTypeFactory'nin ürettiği bir tipin property adı+tipi
        // listesine ERİŞEBİLMESİ için gerekli. Core'un kendisi bunu KULLANMAZ.
        private static readonly ConcurrentDictionary<Type, IReadOnlyList<(string Name, Type Type)>> SchemaByType = new();

        // İsteğe bağlı: şema cache'i sınırsız büyümesin diye basit bir üst sınır.
        // Varsayılan sınırsız (int.MaxValue) - mevcut davranışla birebir uyumlu.
        // NOT: Bu FIFO (ilk giren ilk çıkar) bir sınırlamadır, GERÇEK bir LRU DEĞİLDİR.
        public static int MaxSchemaCacheSize { get; set; } = int.MaxValue;
        private static readonly ConcurrentQueue<string> SchemaInsertionOrder = new();

        /// <summary>
        /// configureType'a geçirilen bilgi paketi: property emisyonundan zaten elde ettiğimiz
        /// get/set MethodBuilder'ları VE metot emisyonundan elde ettiğimiz delegate-field +
        /// forwarder-method çiftleri. TypeBuilder.GetMethod()/GetField() tip CreateType() ile
        /// tamamlanmadan ÇALIŞMADIĞI için (deneyerek bulduk), tek yol bu referansları BAŞTAN
        /// elden ele geçirmek.
        /// </summary>
        public readonly struct TypeMembers
        {
            public IReadOnlyDictionary<string, (MethodBuilder Get, MethodBuilder Set)> Properties { get; }
            public IReadOnlyDictionary<string, (FieldBuilder DelegateField, MethodBuilder Method)> Methods { get; }

            public TypeMembers(
                IReadOnlyDictionary<string, (MethodBuilder Get, MethodBuilder Set)> properties,
                IReadOnlyDictionary<string, (FieldBuilder DelegateField, MethodBuilder Method)> methods)
            {
                Properties = properties;
                Methods = methods;
            }
        }

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
        /// Aynı (className, properties, methods) şeması için CACHE'LENMİŞ tipi döner. İlk
        /// çağrıda IL üretilir, sonraki aynı-şema çağrılarında üretim tekrarlanmaz.
        /// ÖNEMLİ DAVRANIŞ DEĞİŞİKLİĞİ: Eski sürümde her çağrı YENİ bir Type döndürüyordu.
        /// Artık aynı şema aynı Type nesnesini döner. Kesinlikle izole/tekil bir tip
        /// istiyorsanız CreateUniqueType() kullanın.
        /// </summary>
        /// <param name="methods">
        /// Opsiyonel (varsayılan null - mevcut çağrılar hiç etkilenmez). Metot adı -> delegate
        /// tipi (ör. typeof(Func&lt;bool&gt;), typeof(Action&lt;int,string&gt;)). Her giriş için
        /// gerçek bir metot + onu destekleyen bir delegate-field üretilir (bkz. DynamicClass.
        /// AddMethod/SetMethod).
        /// </param>
        /// <param name="configureType">
        /// GENİŞLETME NOKTASI (opsiyonel, varsayılan null - mevcut çağrılar hiç etkilenmez).
        /// Property + metot emisyonu bittikten, ama typeBuilder.CreateType() ile tip
        /// "kilitlenmeden" HEMEN ÖNCE çağrılır. Interface implementasyonu, base class ayarı
        /// gibi ek IL işlemleri için (bkz. DSO.Core.Evoker.Extend) kullanılabilir.
        /// ÖNEMLİ: configureType verildiğinde şema cache'i BYPASS EDİLİR (CreateUniqueType gibi
        /// davranır) - çünkü iki farklı configureType çağrısı AYNI şemayla FARKLI tipler
        /// üretebilir ve şema cache'i bu farkı ayırt edemez.
        /// </param>
        public static Type CreateType(
            string className,
            Dictionary<string, Type> properties,
            Action<TypeBuilder, TypeMembers>? configureType = null,
            Dictionary<string, Type>? methods = null)
        {
            if (configureType != null)
            {
                return EmitType(className, properties, configureType, methods);
            }

            string signature = BuildSignature(className, properties, methods);

            bool added = false;
            Type type = SchemaCache.GetOrAdd(signature, _ =>
            {
                added = true;
                return EmitType(className, properties, null, methods);
            });

            if (added)
            {
                SchemaInsertionOrder.Enqueue(signature);
                TrimSchemaCacheIfNeeded();
            }

            return type;
        }

        /// <summary>
        /// Şema cache'ini BYPASS EDER: her çağrıda kesinlikle yeni, izole bir Type üretir.
        /// Eski CreateType() davranışının karşılığı budur.
        /// </summary>
        public static Type CreateUniqueType(
            string className,
            Dictionary<string, Type> properties,
            Action<TypeBuilder, TypeMembers>? configureType = null,
            Dictionary<string, Type>? methods = null)
        {
            return EmitType(className, properties, configureType, methods);
        }

        /// <summary>
        /// Belirli bir şemayı şema cache'inden çıkarır (Type nesnesinin kendisini silmez,
        /// sadece "bu şema tekrar istenirse yeniden üretilsin" der).
        /// </summary>
        public static bool ForgetSchema(string className, Dictionary<string, Type> properties, Dictionary<string, Type>? methods = null)
        {
            string signature = BuildSignature(className, properties, methods);
            return SchemaCache.TryRemove(signature, out _);
        }

        private static void TrimSchemaCacheIfNeeded()
        {
            while (SchemaCache.Count > MaxSchemaCacheSize && SchemaInsertionOrder.TryDequeue(out var oldestKey))
            {
                SchemaCache.TryRemove(oldestKey, out _);
            }
        }

        private static string BuildSignature(string className, Dictionary<string, Type> properties, Dictionary<string, Type>? methods)
        {
            // Dictionary'nin enumeration sırası garanti değildir; aynı şema farklı sırayla
            // verildiğinde cache miss oluşmaması için isimlere göre sıralıyoruz.
            var sb = new StringBuilder(className);
            foreach (var kvp in properties.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                sb.Append('|').Append(kvp.Key).Append(':').Append(kvp.Value.AssemblyQualifiedName);
            }

            // ÖNEMLİ: methods de imzaya dahil. Aksi halde AYNI properties + FARKLI methods
            // ile iki çağrı yanlışlıkla aynı cache girdisini paylaşır (metot forwarder'ı
            // olmayan bir tip döner) - bunu bilerek burada engelliyoruz.
            if (methods != null && methods.Count > 0)
            {
                sb.Append("||M");
                foreach (var kvp in methods.OrderBy(m => m.Key, StringComparer.Ordinal))
                {
                    sb.Append('|').Append(kvp.Key).Append(':').Append(kvp.Value.AssemblyQualifiedName);
                }
            }

            return sb.ToString();
        }

        private static Type EmitType(
            string className,
            Dictionary<string, Type> properties,
            Action<TypeBuilder, TypeMembers>? configureType,
            Dictionary<string, Type>? methods)
        {
            lock (ModuleLock)
            {
                var moduleBuilder = GetSharedModule();

                // Modül içinde tip adları TEKİL olmak zorunda.
                int id = Interlocked.Increment(ref _typeCounter);
                string internalName = $"{className}_{id}";

                var typeBuilder = moduleBuilder.DefineType(internalName, TypeAttributes.Public | TypeAttributes.Class);
                var propertyAccessors = new Dictionary<string, (MethodBuilder Get, MethodBuilder Set)>();
                var methodForwarders = new Dictionary<string, (FieldBuilder DelegateField, MethodBuilder Method)>();

                foreach (var prop in properties)
                {
                    string propName = prop.Key;
                    Type propType = prop.Value;

                    // NOT (Virtual|NewSlot): metodlar BİLEREK virtual üretiliyor - bir interface
                    // implementasyonu (DefineMethodOverride) CLR tarafında SADECE virtual
                    // metotlarla mümkün. Sıradan kullanımda davranış farkı yaratmaz.
                    FieldBuilder fieldBuilder = typeBuilder.DefineField($"_{propName.ToLower()}", propType, FieldAttributes.Private);
                    PropertyBuilder propertyBuilder = typeBuilder.DefineProperty(propName, PropertyAttributes.HasDefault, propType, null);

                    MethodBuilder getMethodBuilder = typeBuilder.DefineMethod(
                        $"get_{propName}",
                        MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig | MethodAttributes.Virtual | MethodAttributes.NewSlot,
                        propType,
                        Type.EmptyTypes);

                    ILGenerator getIL = getMethodBuilder.GetILGenerator();
                    getIL.Emit(OpCodes.Ldarg_0);
                    getIL.Emit(OpCodes.Ldfld, fieldBuilder);
                    getIL.Emit(OpCodes.Ret);

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

                    propertyAccessors[propName] = (getMethodBuilder, setMethodBuilder);
                }

                if (methods != null)
                {
                    foreach (var m in methods)
                    {
                        methodForwarders[m.Key] = EmitMethodForwarder(typeBuilder, m.Key, m.Value);
                    }
                }

                // GENİŞLETME NOKTASI: emisyon bitti, tip henüz "kilitlenmedi". configureType'a
                // TypeBuilder + zaten ürettiğimiz property/metot bilgilerini veriyoruz.
                configureType?.Invoke(typeBuilder, new TypeMembers(propertyAccessors, methodForwarders));

                Type createdType = typeBuilder.CreateType()!;
                SchemaByType[createdType] = properties.Select(p => (p.Key, p.Value)).ToList();
                return createdType;
            }
        }

        /// <summary>
        /// Bir metot "forwarder"ı üretir: gerçek bir davranış İÇERMEZ, sadece kendi private
        /// delegate-field'ını invoke eder. Field null ise (henüz SetMethod ile bir implementasyon
        /// atanmadıysa) anlaşılır bir InvalidOperationException fırlatır - sessizce
        /// NullReferenceException almak yerine.
        /// </summary>
        private static (FieldBuilder DelegateField, MethodBuilder Method) EmitMethodForwarder(
            TypeBuilder typeBuilder, string methodName, Type delegateType)
        {
            MethodInfo invokeMethod = delegateType.GetMethod("Invoke")
                ?? throw new ArgumentException($"[DynamicTypeFactory] '{delegateType.Name}' geçerli bir delegate tipi değil.");

            ParameterInfo[] parameters = invokeMethod.GetParameters();
            Type[] paramTypes = parameters.Select(p => p.ParameterType).ToArray();
            Type returnType = invokeMethod.ReturnType;

            FieldBuilder fieldBuilder = typeBuilder.DefineField($"_method_{methodName}", delegateType, FieldAttributes.Private);

            MethodBuilder methodBuilder = typeBuilder.DefineMethod(
                methodName,
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual | MethodAttributes.NewSlot,
                returnType,
                paramTypes);

            ILGenerator il = methodBuilder.GetILGenerator();
            Label hasValueLabel = il.DefineLabel();

            // if (_method_X == null) throw new InvalidOperationException("...");
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, fieldBuilder);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Brtrue, hasValueLabel);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldstr,
                $"[DynamicClass] '{methodName}' metodu için implementasyon atanmamış. " +
                $"SetMethod(\"{methodName}\", ...) ile bir delegate atayın.");
            ConstructorInfo exCtor = typeof(InvalidOperationException).GetConstructor(new[] { typeof(string) })!;
            il.Emit(OpCodes.Newobj, exCtor);
            il.Emit(OpCodes.Throw);

            // return _method_X.Invoke(arg1, arg2, ...);
            il.MarkLabel(hasValueLabel);
            for (int i = 0; i < paramTypes.Length; i++)
            {
                il.Emit(OpCodes.Ldarg_S, (byte)(i + 1)); // arg 0 = 'this', argümanlar 1'den başlar
            }
            il.Emit(OpCodes.Callvirt, invokeMethod);
            il.Emit(OpCodes.Ret);

            return (fieldBuilder, methodBuilder);
        }

        /// <summary>
        /// Bu Type, DynamicTypeFactory tarafından mı üretildi?
        /// </summary>
        public static bool IsDynamicType(Type type) => SchemaByType.ContainsKey(type);

        /// <summary>
        /// Bu Type DynamicTypeFactory tarafından üretildiyse property (ad, tip) listesini döner,
        /// üretilmediyse null döner.
        /// </summary>
        public static IReadOnlyList<(string Name, Type Type)>? GetSchema(Type type)
            => SchemaByType.TryGetValue(type, out var schema) ? schema : null;
    }
}