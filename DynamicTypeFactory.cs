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
            public IReadOnlyDictionary<string, (PropertyBuilder Property, MethodBuilder Get, MethodBuilder Set)> Properties { get; }
            public IReadOnlyDictionary<string, (FieldBuilder DelegateField, MethodBuilder Method)> Methods { get; }

            /// <summary>
            /// FAZ 3b: generic metotlar (ör. `T Get&lt;T&gt;()`). Normal Methods'tan AYRI tutulur
            /// çünkü çağrı sözleşmesi farklıdır - burada delegate field'ı her zaman
            /// Func&lt;Type[], object?[], object?&gt;'dir (type-erasure), Func&lt;bool&gt; gibi
            /// somut bir tip DEĞİLDİR (bir generic metot, açık tip parametreleri içerdiği için
            /// tek bir somut delegate tipiyle ifade edilemez - her çağrı farklı T ile yapılabilir).
            /// </summary>
            public IReadOnlyDictionary<string, (FieldBuilder DelegateField, MethodBuilder Method)> GenericMethods { get; }

            /// <summary>FAZ 4c: event'ler (ör. `event EventHandler Changed;`).</summary>
            public IReadOnlyDictionary<string, (FieldBuilder BackingField, MethodBuilder Add, MethodBuilder Remove)> Events { get; }

            public TypeMembers(
                IReadOnlyDictionary<string, (PropertyBuilder Property, MethodBuilder Get, MethodBuilder Set)> properties,
                IReadOnlyDictionary<string, (FieldBuilder DelegateField, MethodBuilder Method)> methods,
                IReadOnlyDictionary<string, (FieldBuilder DelegateField, MethodBuilder Method)> genericMethods,
                IReadOnlyDictionary<string, (FieldBuilder BackingField, MethodBuilder Add, MethodBuilder Remove)> events)
            {
                Properties = properties;
                Methods = methods;
                GenericMethods = genericMethods;
                Events = events;
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
            Dictionary<string, Type>? methods = null,
            Dictionary<string, MethodInfo>? genericMethods = null,
            Dictionary<string, Type>? events = null)
        {
            if (configureType != null)
            {
                return EmitType(className, properties, configureType, methods, genericMethods, events);
            }

            string signature = BuildSignature(className, properties, methods, genericMethods);

            bool added = false;
            Type type = SchemaCache.GetOrAdd(signature, _ =>
            {
                added = true;
                return EmitType(className, properties, null, methods, genericMethods, events);
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
            Dictionary<string, Type>? methods = null,
            Dictionary<string, MethodInfo>? genericMethods = null,
            Dictionary<string, Type>? events = null)
        {
            return EmitType(className, properties, configureType, methods, genericMethods, events);
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

        private static string BuildSignature(string className, Dictionary<string, Type> properties, Dictionary<string, Type>? methods, Dictionary<string, MethodInfo>? genericMethods = null, Dictionary<string, Type>? events = null)
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

            if (genericMethods != null && genericMethods.Count > 0)
            {
                sb.Append("||G");
                foreach (var kvp in genericMethods.OrderBy(m => m.Key, StringComparer.Ordinal))
                {
                    sb.Append('|').Append(kvp.Key).Append(':').Append(kvp.Value.DeclaringType?.AssemblyQualifiedName).Append('.').Append(kvp.Value.ToString());
                }
            }

            if (events != null && events.Count > 0)
            {
                sb.Append("||E");
                foreach (var kvp in events.OrderBy(e => e.Key, StringComparer.Ordinal))
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
            Dictionary<string, Type>? methods,
            Dictionary<string, MethodInfo>? genericMethods = null,
            Dictionary<string, Type>? events = null)
        {
            lock (ModuleLock)
            {
                var moduleBuilder = GetSharedModule();

                // Modül içinde tip adları TEKİL olmak zorunda.
                int id = Interlocked.Increment(ref _typeCounter);
                string internalName = $"{className}_{id}";

                var typeBuilder = moduleBuilder.DefineType(internalName, TypeAttributes.Public | TypeAttributes.Class);
                var propertyAccessors = new Dictionary<string, (PropertyBuilder Property, MethodBuilder Get, MethodBuilder Set)>();
                var methodForwarders = new Dictionary<string, (FieldBuilder DelegateField, MethodBuilder Method)>();
                var genericMethodForwarders = new Dictionary<string, (FieldBuilder DelegateField, MethodBuilder Method)>();
                var eventForwarders = new Dictionary<string, (FieldBuilder BackingField, MethodBuilder Add, MethodBuilder Remove)>();

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

                    propertyAccessors[propName] = (propertyBuilder, getMethodBuilder, setMethodBuilder);
                }

                if (methods != null)
                {
                    foreach (var m in methods)
                    {
                        methodForwarders[m.Key] = EmitMethodForwarder(typeBuilder, m.Key, m.Value);
                    }
                }

                if (genericMethods != null)
                {
                    foreach (var m in genericMethods)
                    {
                        genericMethodForwarders[m.Key] = EmitGenericMethodForwarder(typeBuilder, m.Value);
                    }
                }

                if (events != null)
                {
                    foreach (var e in events)
                    {
                        eventForwarders[e.Key] = EmitEventForwarder(typeBuilder, e.Key, e.Value);
                    }
                }

                // GENİŞLETME NOKTASI: emisyon bitti, tip henüz "kilitlenmedi". configureType'a
                // TypeBuilder + zaten ürettiğimiz property/metot bilgilerini veriyoruz.
                configureType?.Invoke(typeBuilder, new TypeMembers(propertyAccessors, methodForwarders, genericMethodForwarders, eventForwarders));

                Type createdType = typeBuilder.CreateType()!;
                SchemaByType[createdType] = properties.Select(p => (p.Key, p.Value)).ToList();
                return createdType;
            }
        }

        /// <summary>
        /// FAZ 3b: generic bir metot (ör. `T Get&lt;T&gt;()`, `TResult Map&lt;T,TResult&gt;(T input)`)
        /// için forwarder üretir. templateMethod, kopyalanacak generic imzayı (generic parametre
        /// sayısı/adları + onları kullanan parametre/dönüş tipleri) TANIMLAYAN bir MethodInfo'dur
        /// (tipik olarak implement edilen interface'in veya extend edilen base class'ın metodu).
        ///
        /// ÇAĞRI SÖZLEŞMESİ (type-erasure): delegate her zaman
        /// Func&lt;Type[] genericTypeArgs, object?[] args, object? result&gt; şeklindedir.
        /// Bu, generic metotlarda KAÇINILMAZ bir mimari gerçek: bir delegate FIELD'ının tipi
        /// sabittir, ama bir generic metot HER ÇAĞRIDA farklı T ile çağrılabilir - tek bir somut
        /// Func&lt;T,...&gt; bunu ifade edemez. Value-type T'ler için bu, GetValue(string)'teki
        /// gibi kaçınılmaz bir boxing/unboxing getirir.
        ///
        /// ref/out DESTEĞİ: bir parametre ref/out ise, args[i]'ye DOĞRUDAN değeri değil, TEK
        /// ELEMANLI bir "holder" (object[1]) array'i konur. Delegate implementasyonunuz
        /// ((object[])args[i])[0]'ı okuyup/yazarak ref/out semantiğini simüle eder - çağrı
        /// dönünce forwarder holder[0]'ı geri okuyup gerçek ref/out argümanına yazar.
        /// </summary>
        private static (FieldBuilder DelegateField, MethodBuilder Method) EmitGenericMethodForwarder(
            TypeBuilder typeBuilder, MethodInfo templateMethod)
        {
            Type[] templateGenericParams = templateMethod.GetGenericArguments();
            ParameterInfo[] templateParams = templateMethod.GetParameters();

            Type delegateFieldType = typeof(Func<Type[], object?[], object?>);
            FieldBuilder fieldBuilder = typeBuilder.DefineField($"_genericmethod_{templateMethod.Name}", delegateFieldType, FieldAttributes.Private);

            MethodBuilder methodBuilder = typeBuilder.DefineMethod(
                templateMethod.Name,
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual | MethodAttributes.NewSlot);

            GenericTypeParameterBuilder[] newGenericParams = templateGenericParams.Length > 0
                ? methodBuilder.DefineGenericParameters(templateGenericParams.Select(t => t.Name).ToArray())
                : Array.Empty<GenericTypeParameterBuilder>();

            Type SubstituteType(Type type)
            {
                bool byref = type.IsByRef;
                Type t = byref ? type.GetElementType()! : type;

                int idx = Array.IndexOf(templateGenericParams, t);
                Type result;
                if (idx >= 0)
                {
                    result = newGenericParams[idx];
                }
                else if (t.IsGenericType && !t.IsGenericTypeDefinition)
                {
                    var args = t.GetGenericArguments().Select(SubstituteType).ToArray();
                    result = t.GetGenericTypeDefinition().MakeGenericType(args);
                }
                else if (t.IsArray)
                {
                    var elem = SubstituteType(t.GetElementType()!);
                    result = t.GetArrayRank() == 1 ? elem.MakeArrayType() : elem.MakeArrayType(t.GetArrayRank());
                }
                else
                {
                    result = t; // template'in generic parametrelerine bağlı olmayan sıradan bir tip
                }

                return byref ? result.MakeByRefType() : result;
            }

            Type returnType = SubstituteType(templateMethod.ReturnType);
            Type[] paramTypes = templateParams.Select(p => SubstituteType(p.ParameterType)).ToArray();

            methodBuilder.SetSignature(returnType, null, null, paramTypes.Length > 0 ? paramTypes : null, null, null);
            for (int i = 0; i < templateParams.Length; i++)
            {
                methodBuilder.DefineParameter(i + 1, ParameterAttributes.None, templateParams[i].Name);
            }

            ILGenerator il = methodBuilder.GetILGenerator();

            // ÖNEMLİ BULGU: generic parametreli bir metodun IL'inde doğrudan bir dallanma+throw
            // (branch to a label that ends in Newobj+Throw) kullanmak bu runtime'da
            // InvalidProgramException'a yol açıyor - deneyerek bulduk (aynı desen, generic
            // OLMAYAN forwarder'da sorunsuz çalışıyor). Çözüm: null kontrolünü IL'İN İÇİNE
            // gömmek yerine normal (Reflection.Emit'siz) bir C# yardımcı metoduna (aşağıdaki
            // InvokeGenericMethodDelegate) taşıdık - JIT onu sorunsuz derliyor, generic
            // forwarder'ın IL'i ise HİÇ dallanmadan düz bir çizgi (her zaman yardımcıyı çağırır,
            // o null ise KENDİSİ throw eder).
            LocalBuilder typeArgsLocal = il.DeclareLocal(typeof(Type[]));
            il.Emit(OpCodes.Ldc_I4, newGenericParams.Length);
            il.Emit(OpCodes.Newarr, typeof(Type));
            for (int i = 0; i < newGenericParams.Length; i++)
            {
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Ldc_I4, i);
                il.Emit(OpCodes.Ldtoken, newGenericParams[i]);
                il.Emit(OpCodes.Call, typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle))!);
                il.Emit(OpCodes.Stelem_Ref);
            }
            il.Emit(OpCodes.Stloc, typeArgsLocal);

            // object?[] args = { p1, p2, ... }  (value type'lar BOX'lanır). ref/out parametreler
            // için args[i]'ye TEK ELEMANLI bir "holder" (object[1]) konur - bkz. sınıf dokümanı.
            LocalBuilder argsLocal = il.DeclareLocal(typeof(object[]));
            var holderLocals = new LocalBuilder?[paramTypes.Length];
            var elemTypes = new Type[paramTypes.Length];
            var isByRefParam = new bool[paramTypes.Length];

            il.Emit(OpCodes.Ldc_I4, paramTypes.Length);
            il.Emit(OpCodes.Newarr, typeof(object));
            for (int i = 0; i < paramTypes.Length; i++)
            {
                Type pt = paramTypes[i];
                bool byref = pt.IsByRef;
                isByRefParam[i] = byref;
                Type elemType = byref ? pt.GetElementType()! : pt;
                elemTypes[i] = elemType;

                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Ldc_I4, i);

                if (!byref)
                {
                    il.Emit(OpCodes.Ldarg_S, (byte)(i + 1));
                    if (elemType.IsValueType || elemType.IsGenericParameter)
                    {
                        // elemType bir generic parametre OLABİLİR (T) - Box IL'i generic
                        // parametreler için de geçerlidir, JIT runtime'da gerçek tipe göre karar verir.
                        il.Emit(OpCodes.Box, elemType);
                    }
                    il.Emit(OpCodes.Stelem_Ref);
                }
                else
                {
                    bool isOut = templateParams[i].IsOut;
                    LocalBuilder holderLocal = il.DeclareLocal(typeof(object[]));
                    holderLocals[i] = holderLocal;

                    il.Emit(OpCodes.Ldc_I4_1);
                    il.Emit(OpCodes.Newarr, typeof(object));

                    if (!isOut)
                    {
                        // out DEĞİLSE (ref veya in) MEVCUT değeri holder[0]'a oku.
                        il.Emit(OpCodes.Dup);
                        il.Emit(OpCodes.Ldc_I4_0);
                        il.Emit(OpCodes.Ldarg_S, (byte)(i + 1)); // managed pointer
                        il.Emit(OpCodes.Ldobj, elemType);        // dereference
                        if (elemType.IsValueType || elemType.IsGenericParameter)
                        {
                            il.Emit(OpCodes.Box, elemType);
                        }
                        il.Emit(OpCodes.Stelem_Ref);
                    }

                    il.Emit(OpCodes.Stloc, holderLocal);
                    il.Emit(OpCodes.Ldloc, holderLocal); // args[i] = holder
                    il.Emit(OpCodes.Stelem_Ref);
                }
            }
            il.Emit(OpCodes.Stloc, argsLocal);

            // InvokeGenericMethodDelegate(delegateField, metotAdı, typeArgs, args) - null
            // kontrolü ve throw BURADA (normal C#'ta), IL'de DEĞİL.
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, fieldBuilder);
            il.Emit(OpCodes.Ldstr, templateMethod.Name);
            il.Emit(OpCodes.Ldloc, typeArgsLocal);
            il.Emit(OpCodes.Ldloc, argsLocal);
            il.Emit(OpCodes.Call, typeof(DynamicTypeFactory).GetMethod(nameof(InvokeGenericMethodDelegate))!);

            // Sonucu bir local'e al - byref writeback'lerini yaparken stack'i karıştırmamak için.
            LocalBuilder resultLocal = il.DeclareLocal(typeof(object));
            il.Emit(OpCodes.Stloc, resultLocal);

            // ref/out parametrelerini holder[0]'dan GERİ OKUYUP gerçek argümana yaz.
            for (int i = 0; i < paramTypes.Length; i++)
            {
                if (!isByRefParam[i]) continue;

                il.Emit(OpCodes.Ldarg_S, (byte)(i + 1)); // ptr
                il.Emit(OpCodes.Ldloc, holderLocals[i]!);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ldelem_Ref);              // holder[0] (object)
                il.Emit(OpCodes.Unbox_Any, elemTypes[i]);
                il.Emit(OpCodes.Stobj, elemTypes[i]);
            }

            il.Emit(OpCodes.Ldloc, resultLocal);
            if (returnType == typeof(void))
            {
                il.Emit(OpCodes.Pop);
            }
            else
            {
                // Unbox_Any hem value type hem reference type dönüş tipleri için ÇALIŞIR
                // (reference type'ta güvenli bir cast'e eşdeğerdir) - tek IL'de ikisini de kapsar.
                il.Emit(OpCodes.Unbox_Any, returnType);
            }
            il.Emit(OpCodes.Ret);

            return (fieldBuilder, methodBuilder);
        }

        /// <summary>
        /// Generic metot forwarder'larının IL'den ÇAĞIRDIĞI yardımcı - null kontrolü ve throw
        /// burada, normal C#'ta yapılıyor (IL içine gömülü bir branch+throw, generic metotlarda
        /// bu runtime'da InvalidProgramException'a yol açıyordu - deneyerek bulduk).
        /// </summary>
        public static object? InvokeGenericMethodDelegate(
            Func<Type[], object?[], object?>? implementation, string methodName, Type[] typeArgs, object?[] args)
        {
            if (implementation == null)
            {
                throw new InvalidOperationException(
                    $"[DynamicClass] '{methodName}' (generic) metodu için implementasyon atanmamış. " +
                    $"SetGenericMethod(\"{methodName}\", ...) ile bir delegate atayın.");
            }

            return implementation(typeArgs, args);
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
        /// FAZ 4c: bir event için add_X/remove_X forwarder'ı + gerçek bir CLR event'i üretir.
        /// Standart C# derleyicisinin auto-event'ler için ürettiği desene benzer
        /// (Delegate.Combine/Remove), ama BASİTLEŞTİRİLMİŞ: Interlocked.CompareExchange
        /// tabanlı thread-safe versiyon DEĞİL - aynı event'e EŞ ZAMANLI add/remove çağrılırsa
        /// (nadir bir senaryo) bir güncelleme kaybolabilir. Tek thread'den add/remove için
        /// tamamen doğru ve yeterlidir.
        /// </summary>
        private static (FieldBuilder BackingField, MethodBuilder Add, MethodBuilder Remove) EmitEventForwarder(
            TypeBuilder typeBuilder, string eventName, Type handlerType)
        {
            FieldBuilder fieldBuilder = typeBuilder.DefineField($"_event_{eventName}", handlerType, FieldAttributes.Private);

            MethodInfo combine = typeof(Delegate).GetMethod(nameof(Delegate.Combine), new[] { typeof(Delegate), typeof(Delegate) })!;
            MethodInfo remove = typeof(Delegate).GetMethod(nameof(Delegate.Remove), new[] { typeof(Delegate), typeof(Delegate) })!;

            MethodBuilder addMethod = typeBuilder.DefineMethod(
                $"add_{eventName}",
                MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig | MethodAttributes.Virtual | MethodAttributes.NewSlot,
                typeof(void), new[] { handlerType });
            {
                ILGenerator il = addMethod.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, fieldBuilder);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Call, combine);
                il.Emit(OpCodes.Castclass, handlerType);
                il.Emit(OpCodes.Stfld, fieldBuilder);
                il.Emit(OpCodes.Ret);
            }

            MethodBuilder removeMethod = typeBuilder.DefineMethod(
                $"remove_{eventName}",
                MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig | MethodAttributes.Virtual | MethodAttributes.NewSlot,
                typeof(void), new[] { handlerType });
            {
                ILGenerator il = removeMethod.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, fieldBuilder);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Call, remove);
                il.Emit(OpCodes.Castclass, handlerType);
                il.Emit(OpCodes.Stfld, fieldBuilder);
                il.Emit(OpCodes.Ret);
            }

            EventBuilder eventBuilder = typeBuilder.DefineEvent(eventName, EventAttributes.None, handlerType);
            eventBuilder.SetAddOnMethod(addMethod);
            eventBuilder.SetRemoveOnMethod(removeMethod);

            return (fieldBuilder, addMethod, removeMethod);
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