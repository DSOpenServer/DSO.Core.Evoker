using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading.Tasks;

namespace DSO.Core.Evoker
{
    /// <summary>
    /// AddProperty / SetValue&lt;T&gt; / GetValue&lt;T&gt; tarzı basit, akıcı (fluent) bir
    /// kullanım sağlayan sarmalayıcı. Alttan alta hâlâ DynamicTypeFactory + DynamicEntityAccessor
    /// + EvokerBuilder kullanır; bu sınıf sadece "önce şema tanımla, sonra değer oku/yaz/metot
    /// çağır" akışını TEK bir değişken üzerinden yönetmenizi sağlar.
    ///
    /// ÖNEMLİ TASARIM KARARI (cache ömrü):
    /// Üretilen Type ve SetValue/GetValue için derlenen accessor delegate'leri bu nesnenin
    /// KENDİSİNE değil, DynamicTypeFactory/DynamicEntityAccessor'ın PAYLAŞIMLI, process-ömürlü
    /// cache'lerine aittir. "yeniClass" scope dışına çıkıp GC'lendiğinde SADECE o tek instance
    /// çöpe gider; şema + derlenmiş delegate'ler cache'te KALIR.
    ///
    /// Şemanız GERÇEKTEN bir kerelik/tekrar etmeyecekse (ör. her çağrıda rastgele farklı kolon
    /// seti), useSchemaCache:false + forgetOnDispose:true verin ve using/Dispose ile temizleyin -
    /// aksi halde cache sessizce, sınırsız büyür.
    ///
    /// THREAD-SAFETY: Şema tanımlama metotları (AddProperty/AddMethod/AddGenericMethod/
    /// WithTypeConfigurator) ve build (EnsureBuilt) bir `_buildLock` ile korunuyor - aynı
    /// DynamicClass'ı birden fazla thread'den EŞ ZAMANLI kurmaya çalışsanız bile Type/instance
    /// SADECE BİR KEZ üretilir. OnSet/OnGet hook'ları build SONRASINDA bile eklenebildiği ve
    /// SetValue/GetValue'nun (hot path) her çağrısında okunduğu için ConcurrentDictionary
    /// kullanılıyor - kaba bir kilit yerine, hot path'i yavaşlatmayan ince-taneli senkronizasyon.
    /// SetValue/GetValue'nun KENDİSİ (aynı property'ye eşzamanlı yazma) senkronize DEĞİLDİR -
    /// bu, herhangi bir paylaşımlı mutable nesneyle aynı, normal .NET semantiğidir; eşzamanlı
    /// yazma güvenliği gerekiyorsa çağıran taraf kendi senkronizasyonunu eklemelidir.
    /// </summary>
    public sealed class DynamicClass : IDisposable
    {
        private readonly object _buildLock = new();
        private readonly string _className;
        private readonly Dictionary<string, Type> _properties = new();
        private readonly Dictionary<string, Type> _methods = new(); // metot adı -> delegate tipi
        private readonly Dictionary<string, MethodInfo> _genericMethods = new(); // metot adı -> template MethodInfo (generic imzayı tanımlar)
        private readonly Dictionary<string, Type> _events = new(); // event adı -> handler delegate tipi
        private readonly bool _useSchemaCache;
        private readonly bool _forgetOnDispose;

        private volatile Type? _type;
        private object? _instance;
        private bool _disposed;

        // Hook'lar - INSTANCE'A ÖZEL (global DynamicEntityAccessor cache'ine DEĞİL). Build
        // sonrasında bile eklenip SetValue/GetValue'nun hot path'inden okunduğu için
        // ConcurrentDictionary - global bir kilit yerine ince-taneli senkronizasyon.
        private ConcurrentDictionary<string, Delegate>? _onSetHooks;
        private ConcurrentDictionary<string, Delegate>? _onGetHooks;

        // DSO.Core.Evoker.Extend için GENİŞLETME NOKTASI - core bunu HİÇ KULLANMAZ.
        private Action<TypeBuilder, DynamicTypeFactory.TypeMembers>? _configureType;

        private DynamicClass(string className, bool useSchemaCache, bool forgetOnDispose)
        {
            if (forgetOnDispose && useSchemaCache)
            {
                throw new ArgumentException(
                    "[DynamicClass] forgetOnDispose:true ile useSchemaCache:true birlikte kullanılamaz. " +
                    "useSchemaCache:true demek bu şema BAŞKA DynamicClass örnekleriyle PAYLAŞILIYOR demektir; " +
                    "Dispose() sırasında unutmak, o diğer örnekleri de etkiler ve sessiz yavaşlamaya yol açar. " +
                    "Gerçekten izole/tek-kullanımlık bir tip istiyorsanız useSchemaCache:false verin.");
            }

            _className = className;
            _useSchemaCache = useSchemaCache;
            _forgetOnDispose = forgetOnDispose;
        }

        public static DynamicClass CreateClass(string? className = null, bool useSchemaCache = true, bool forgetOnDispose = false)
            => new DynamicClass(className ?? $"Dynamic_{Guid.NewGuid():N}", useSchemaCache, forgetOnDispose);

        /// <summary>
        /// VAR OLAN bir DynamicTypeFactory tipini ve o tipten bir instance'ı DynamicClass API'sine
        /// sarmalar. AddProperty/AddMethod/build akışını hiç ATLAR - tip zaten üretilmiş, instance
        /// zaten var. Tipik kullanım: bir deserialize işleminden (ör. DSO.Core.Evoker.Json) veya
        /// başka bir kod yolundan elinize geçen "çıplak" bir dynamic-type instance'ını, SetValue/
        /// GetValue/InvokeMethod gibi rahat API'lerle kullanmak istediğinizde.
        /// </summary>
        public static DynamicClass Wrap(Type type, object instance)
        {
            if (!DynamicTypeFactory.IsDynamicType(type))
            {
                throw new ArgumentException(
                    $"[DynamicClass] '{type.Name}' DynamicTypeFactory tarafından üretilmemiş, Wrap() ile sarmalanamaz.",
                    nameof(type));
            }

            if (!type.IsInstanceOfType(instance))
            {
                throw new ArgumentException(
                    $"[DynamicClass] Verilen instance '{type.Name}' tipinde değil (gerçek tip: '{instance.GetType().Name}').",
                    nameof(instance));
            }

            var dc = new DynamicClass(type.Name, useSchemaCache: true, forgetOnDispose: false);
            dc._type = type;       // _type != null olduğu için EnsureBuilt() bir daha build ETMEZ
            dc._instance = instance;
            return dc;
        }

        public DynamicClass AddProperty(string name, Type type)
        {
            lock (_buildLock)
            {
                EnsureNotBuilt();

                if (_properties.TryGetValue(name, out var existingType) && existingType != type)
                {
                    throw new InvalidOperationException(
                        $"[DynamicClass] '{name}' property'si zaten '{existingType.Name}' tipiyle eklenmiş, " +
                        $"'{type.Name}' ile TEKRAR eklenemez. Aynı isimle farklı tipte iki AddProperty çağrısı " +
                        "büyük olasılıkla bir hata işaretidir.");
                }

                _properties[name] = type;
                return this;
            }
        }

        public DynamicClass AddProperty<T>(string name) => AddProperty(name, typeof(T));

        /// <summary>
        /// FAZ 3: Şemaya gerçek bir METOT ekler. TDelegate metodun tam imzasını belirler
        /// (ör. Func&lt;bool&gt;, Action&lt;int,string&gt;). Metodun gövdesi henüz yoktur -
        /// SetMethod ile sonradan (veya hemen) bir implementasyon atamanız gerekir; atamadan
        /// çağırırsanız InvalidOperationException alırsınız (sessiz NullReferenceException değil).
        /// </summary>
        public DynamicClass AddMethod<TDelegate>(string methodName) where TDelegate : Delegate
            => AddMethod(methodName, typeof(TDelegate));

        /// <summary>
        /// AddMethod'un non-generic hali. Delegate tipi COMPILE-TIME'da bilinmediğinde
        /// (ör. Extend'in `ref`/`out` içeren bir interface metodu için RUNTIME'DA sentezlediği
        /// bir delegate tipi) kullanılır.
        /// </summary>
        public DynamicClass AddMethod(string methodName, Type delegateType)
        {
            lock (_buildLock)
            {
                EnsureNotBuilt();

                if (!typeof(Delegate).IsAssignableFrom(delegateType))
                {
                    throw new ArgumentException($"[DynamicClass] '{delegateType.Name}' bir delegate tipi değil.", nameof(delegateType));
                }

                if (_methods.TryGetValue(methodName, out var existingType) && existingType != delegateType)
                {
                    throw new InvalidOperationException(
                        $"[DynamicClass] '{methodName}' metodu zaten '{existingType.Name}' imzasıyla eklenmiş, " +
                        $"'{delegateType.Name}' ile TEKRAR eklenemez.");
                }

                _methods[methodName] = delegateType;
                return this;
            }
        }

        /// <summary>
        /// AddMethod ile eklenen bir metodun BEKLEDİĞİ delegate tipini döner. `ref`/`out`
        /// parametreli metotlarda bu tip Func&lt;...&gt;/Action&lt;...&gt; DEĞİL, runtime'da
        /// üretilmiş özel bir delegate tipidir (bkz. DynamicDelegateTypeFactory) - bir lambda ile
        /// doğrudan yazamazsınız, gerçek bir metodu Delegate.CreateDelegate ile bağlamanız gerekir:
        /// <code>
        /// var del = Delegate.CreateDelegate(dc.GetMethodDelegateType("TryParse"), null, gerçekMetot);
        /// dc.SetMethod("TryParse", del);
        /// </code>
        /// </summary>
        public Type GetMethodDelegateType(string methodName)
        {
            if (!_methods.TryGetValue(methodName, out var delegateType))
            {
                throw new MissingMemberException($"[DynamicClass] '{methodName}' AddMethod ile eklenmemiş.");
            }
            return delegateType;
        }

        /// <summary>
        /// AddMethod ile eklenmiş (veya Implement/Extend ile otomatik eklenmiş) bir metoda
        /// gerçek implementasyonu atar. Build'den ÖNCE veya SONRA çağrılabilir - build'den
        /// önce çağrılırsa build'i tetikler.
        /// </summary>
        public DynamicClass SetMethod<TDelegate>(string methodName, TDelegate implementation) where TDelegate : Delegate
            => SetMethod(methodName, (Delegate)implementation);

        /// <summary>
        /// SetMethod'un non-generic hali. `ref`/`out` parametreli metotlar için gereklidir -
        /// böyle bir metodun delegate tipi runtime'da sentezlendiğinden, bir C# lambda'sını
        /// doğrudan TDelegate'e bağlayamazsınız (bkz. GetMethodDelegateType).
        /// </summary>
        public DynamicClass SetMethod(string methodName, Delegate implementation)
        {
            EnsureBuilt();

            FieldInfo field = _type!.GetField($"_method_{methodName}", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new MissingMemberException(
                    $"[DynamicClass] '{methodName}' için metot alanı bulunamadı. AddMethod(\"{methodName}\", ...) ile eklediniz mi?");

            if (!field.FieldType.IsInstanceOfType(implementation))
            {
                throw new ArgumentException(
                    $"[DynamicClass] '{methodName}' için verilen delegate tipi ('{implementation.GetType().Name}') " +
                    $"beklenen tiple ('{field.FieldType.Name}') uyuşmuyor.", nameof(implementation));
            }

            field.SetValue(_instance, implementation);
            return this;
        }

        /// <summary>
        /// FAZ 3b: Şemaya GENERİC bir metot ekler (ör. `T Get&lt;T&gt;()`). templateMethod,
        /// kopyalanacak generic imzayı (generic parametreler + onları kullanan parametre/dönüş
        /// tipleri) tanımlayan bir MethodInfo'dur - tipik olarak Extend'in bir interface/base
        /// class'tan otomatik türettiği metot. Standalone (interface'siz) kullanım için kendi
        /// MethodInfo'nuzu (ör. bir örnek/template sınıftaki generic bir metot) verebilirsiniz.
        /// </summary>
        public DynamicClass AddGenericMethod(MethodInfo templateMethod)
        {
            lock (_buildLock)
            {
                EnsureNotBuilt();
                _genericMethods[templateMethod.Name] = templateMethod;
                return this;
            }
        }

        /// <summary>
        /// AddGenericMethod ile eklenmiş generic bir metoda implementasyon atar. Çağrı
        /// sözleşmesi TYPE-ERASURE'dır: implementation, metot HANGİ T ile çağrılırsa çağrılsın
        /// aynı delegate'tir - generic tip argümanları (typeArgs[0], typeArgs[1], ...) ve
        /// argüman değerleri (args[0], args[1], ...) size PARAMETRE olarak gelir, siz de
        /// sonucu object olarak (value type'sa BOX'layarak) döndürürsünüz. Bu, tek bir
        /// delegate field'ının HER ÇAĞRIDA farklı T ile çalışabilmesinin TEK yoludur.
        ///
        /// `ref`/`out` PARAMETRELER: args[i] normal bir değer DEĞİL, TEK ELEMANLI bir "holder"
        /// (object?[1]) olur - ((object?[])args[i])[0] üzerinden okuyup/yazarsınız. ÖNEMLİ
        /// SÖZLEŞME: T bir value type olabileceği için holder[0]'a ASLA çıplak null koymayın
        /// (forwarder onu Unbox_Any ile açar, null verirseniz NullReferenceException alırsınız) -
        /// "değer yok" durumunda bile typeArgs[i]'nin geçerli bir boxlanmış varsayılan değerini
        /// koyun (value type için Activator.CreateInstance(typeArgs[i]), reference type için
        /// null güvenlidir).
        /// </summary>
        public DynamicClass SetGenericMethod(string methodName, Func<Type[], object?[], object?> implementation)
        {
            EnsureBuilt();

            FieldInfo field = _type!.GetField($"_genericmethod_{methodName}", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new MissingMemberException(
                    $"[DynamicClass] '{methodName}' için generic metot alanı bulunamadı. AddGenericMethod(...) ile eklediniz mi?");

            field.SetValue(_instance, implementation);
            return this;
        }

        /// <summary>
        /// FAZ 4c: Şemaya gerçek bir CLR EVENT'i ekler (ör. `event EventHandler Changed;`).
        /// THandler tipik olarak EventHandler/EventHandler&lt;T&gt; veya kendi delegate'inizdir.
        /// Üretilen event, standart C# add/remove semantiğine sahiptir (Delegate.Combine/Remove) -
        /// dc.As&lt;TInterface&gt;().Changed += handler; gibi NORMAL C# event syntax'ıyla
        /// abone olunabilir/çıkılabilir.
        /// </summary>
        public DynamicClass AddEvent<THandler>(string eventName) where THandler : Delegate
            => AddEvent(eventName, typeof(THandler));

        /// <summary>AddEvent'in non-generic hali (handler tipi runtime'da bilindiğinde).</summary>
        public DynamicClass AddEvent(string eventName, Type handlerType)
        {
            lock (_buildLock)
            {
                EnsureNotBuilt();

                if (!typeof(Delegate).IsAssignableFrom(handlerType))
                {
                    throw new ArgumentException($"[DynamicClass] '{handlerType.Name}' bir delegate tipi değil.", nameof(handlerType));
                }

                if (_events.TryGetValue(eventName, out var existingType) && existingType != handlerType)
                {
                    throw new InvalidOperationException(
                        $"[DynamicClass] '{eventName}' event'i zaten '{existingType.Name}' tipiyle eklenmiş, " +
                        $"'{handlerType.Name}' ile TEKRAR eklenemez.");
                }

                _events[eventName] = handlerType;
                return this;
            }
        }

        /// <summary>
        /// Event'i İÇERİDEN (ör. bir SetMethod delegate'i içinden, ya da bir property değiştiğinde)
        /// tetiklemek için. Event'e hiç abone olunmadıysa (backing field null) sessizce hiçbir şey
        /// yapmaz - normal C# event semantiğiyle tutarlı ("kimse dinlemiyorsa tetiklemenin bir
        /// zararı yok").
        /// </summary>
        public DynamicClass RaiseEvent(string eventName, params object?[] args)
        {
            EnsureBuilt();

            FieldInfo field = _type!.GetField($"_event_{eventName}", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new MissingMemberException(
                    $"[DynamicClass] '{eventName}' için event alanı bulunamadı. AddEvent(...) ile eklediniz mi?");

            var handler = field.GetValue(_instance) as Delegate;
            handler?.DynamicInvoke(args);

            return this;
        }

        /// <summary>
        /// Bir metodu isimle çağırır (dönüş değeri olan). Alttan EvokerBuilder kullanır - hem
        /// AddMethod ile eklediğiniz (delegate-forward) metotlar hem de (Extend&lt;TBase&gt;
        /// ile) miras alınan SOMUT base class metotları için çalışır.
        /// </summary>
        public TReturn? InvokeMethod<TReturn>(string methodName, params object[] args)
        {
            EnsureBuilt();
            return new EvokerBuilder(_type!).SetInstance(_instance!).Invoke<TReturn>(methodName, args);
        }

        /// <summary>Dönüş değeri olmayan (void) bir metodu isimle çağırır.</summary>
        public DynamicClass InvokeMethod(string methodName, params object[] args)
        {
            EnsureBuilt();
            new EvokerBuilder(_type!).SetInstance(_instance!).Execute(methodName, args);
            return this;
        }

        public async Task<TReturn?> InvokeMethodAsync<TReturn>(string methodName, params object[] args)
        {
            EnsureBuilt();
            return await new EvokerBuilder(_type!).SetInstance(_instance!).InvokeAsync<TReturn>(methodName, args);
        }

        public DynamicClass WithTypeConfigurator(Action<TypeBuilder, DynamicTypeFactory.TypeMembers> configurator)
        {
            lock (_buildLock)
            {
                EnsureNotBuilt();
                if (_configureType == null)
                {
                    _configureType = configurator;
                }
                else
                {
                    var previous = _configureType;
                    _configureType = (tb, members) => { previous(tb, members); configurator(tb, members); };
                }
                return this;
            }
        }

        // FAZ 4: Attribute enjeksiyonu (ör. [Obsolete], [Required] veya kendi custom attribute'unuz).
        private readonly List<CustomAttributeBuilder> _typeAttributes = new();
        private readonly Dictionary<string, List<CustomAttributeBuilder>> _propertyAttributes = new();
        private bool _attributeConfiguratorRegistered;

        /// <summary>Üretilen TİPİN kendisine bir attribute ekler (ör. [Serializable]).</summary>
        public DynamicClass AddTypeAttribute<TAttribute>(params object[] constructorArgs) where TAttribute : Attribute
        {
            lock (_buildLock)
            {
                EnsureNotBuilt();
                var ctor = ResolveAttributeConstructor(typeof(TAttribute), constructorArgs);
                _typeAttributes.Add(new CustomAttributeBuilder(ctor, constructorArgs));
                RegisterAttributeConfiguratorIfNeeded();
                return this;
            }
        }

        /// <summary>Belirli bir PROPERTY'e bir attribute ekler (ör. [JsonPropertyName("id")]).</summary>
        public DynamicClass AddPropertyAttribute<TAttribute>(string propertyName, params object[] constructorArgs) where TAttribute : Attribute
        {
            lock (_buildLock)
            {
                EnsureNotBuilt();
                var ctor = ResolveAttributeConstructor(typeof(TAttribute), constructorArgs);
                if (!_propertyAttributes.TryGetValue(propertyName, out var list))
                {
                    list = new List<CustomAttributeBuilder>();
                    _propertyAttributes[propertyName] = list;
                }
                list.Add(new CustomAttributeBuilder(ctor, constructorArgs));
                RegisterAttributeConfiguratorIfNeeded();
                return this;
            }
        }

        private static ConstructorInfo ResolveAttributeConstructor(Type attributeType, object[] constructorArgs)
        {
            var ctorTypes = constructorArgs.Select(a => a?.GetType() ?? typeof(object)).ToArray();
            return attributeType.GetConstructor(ctorTypes)
                ?? throw new MissingMethodException(
                    $"[DynamicClass] '{attributeType.Name}' için ({string.Join(", ", ctorTypes.Select(t => t.Name))}) " +
                    "parametreleriyle uyumlu bir constructor bulunamadı.");
        }

        private void RegisterAttributeConfiguratorIfNeeded()
        {
            // NOT: bu metot zaten _buildLock TUTULURKEN çağrılıyor (AddTypeAttribute/
            // AddPropertyAttribute içinden). WithTypeConfigurator de aynı kilidi alıyor - ama
            // C#'ın lock'ı (Monitor) AYNI THREAD için REENTRANT olduğundan burada deadlock OLMAZ.
            if (_attributeConfiguratorRegistered) return;
            _attributeConfiguratorRegistered = true;

            WithTypeConfigurator((typeBuilder, members) =>
            {
                foreach (var attr in _typeAttributes)
                {
                    typeBuilder.SetCustomAttribute(attr);
                }

                foreach (var kvp in _propertyAttributes)
                {
                    if (!members.Properties.TryGetValue(kvp.Key, out var accessors))
                    {
                        throw new InvalidOperationException(
                            $"[DynamicClass] '{kvp.Key}' property'si bulunamadı, attribute eklenemedi.");
                    }

                    foreach (var attr in kvp.Value)
                    {
                        accessors.Property.SetCustomAttribute(attr);
                    }
                }
            });
        }

        public DynamicClass Build()
        {
            EnsureBuilt();
            return this;
        }

        /// <summary>
        /// Soğuk başlangıç JIT/derleme maliyetini öne çekmek için: property accessor'larını
        /// (DynamicEntityAccessor) ve AddMethod ile eklenen metotların EvokerBuilder cache'ini
        /// ÖNCEDEN derler. Uygulama açılışında, gerçek trafik başlamadan önce çağırın.
        /// NOT: AddGenericMethod ile eklenen metotlar ısıtılmıyor - EvokerBuilder generic metot
        /// çağırmayı desteklemiyor (bkz. bilinen kısıtlar), bu yüzden onlar için warmup'ın
        /// karşılığı yok; generic metotların IL'i zaten Type oluşturulurken (EmitType) tek
        /// seferlik üretiliyor, kalan tek maliyet ilk çağrıdaki normal JIT'tir.
        /// </summary>
        public DynamicClass Warmup()
        {
            EnsureBuilt();

            foreach (var (name, propType) in DynamicTypeFactory.GetSchema(_type!) ?? Array.Empty<(string, Type)>())
            {
                var getGetter = typeof(DynamicEntityAccessor).GetMethod(nameof(DynamicEntityAccessor.GetGetter))!.MakeGenericMethod(propType);
                getGetter.Invoke(null, new object[] { _type!, name });

                var getSetter = typeof(DynamicEntityAccessor).GetMethod(nameof(DynamicEntityAccessor.GetSetter))!.MakeGenericMethod(propType);
                getSetter.Invoke(null, new object[] { _type!, name });
            }

            var builder = new EvokerBuilder(_type!).SetInstance(_instance!);
            foreach (var (methodName, delegateType) in _methods)
            {
                MethodInfo invokeMethod = delegateType.GetMethod("Invoke")!;
                Type returnType = invokeMethod.ReturnType;

                if (returnType == typeof(void))
                {
                    builder.GetAction(methodName);
                }
                else
                {
                    // EvokerBuilder'ın cache'i (Type, Metot, DÖNÜŞ TİPİ) üzerinden anahtarlanıyor -
                    // ısınmanın işe yaraması için GERÇEK dönüş tipiyle ısıtmak gerekiyor, sabit
                    // <object> ile değil (aksi halde farklı bir cache girdisini ısıtmış oluruz).
                    var getFunc = typeof(EvokerBuilder).GetMethod(nameof(EvokerBuilder.GetFunc))!.MakeGenericMethod(returnType);
                    getFunc.Invoke(builder, new object?[] { methodName, null });
                }
            }

            return this;
        }

        public DynamicClass SetValue<T>(string propertyName, T value)
        {
            EnsureBuilt();

            if (_onSetHooks != null && _onSetHooks.TryGetValue(propertyName, out var hookDel) && hookDel is Action<T, T> hook)
            {
                T oldValue = DynamicEntityAccessor.GetGetter<T>(_type!, propertyName)(_instance!);
                DynamicEntityAccessor.GetSetter<T>(_type!, propertyName)(_instance!, value);
                hook(oldValue, value);
            }
            else
            {
                DynamicEntityAccessor.GetSetter<T>(_type!, propertyName)(_instance!, value);
            }

            return this;
        }

        public T GetValue<T>(string propertyName)
        {
            EnsureBuilt();
            T value = DynamicEntityAccessor.GetGetter<T>(_type!, propertyName)(_instance!);

            if (_onGetHooks != null && _onGetHooks.TryGetValue(propertyName, out var hookDel) && hookDel is Action<T> hook)
            {
                hook(value);
            }

            return value;
        }

        public DynamicClass OnSet<T>(string propertyName, Action<T, T> onChanged)
        {
            if (_onSetHooks == null)
            {
                lock (_buildLock) { _onSetHooks ??= new ConcurrentDictionary<string, Delegate>(); }
            }
            _onSetHooks![propertyName] = onChanged;
            return this;
        }

        public DynamicClass OnGet<T>(string propertyName, Action<T> onRead)
        {
            if (_onGetHooks == null)
            {
                lock (_buildLock) { _onGetHooks ??= new ConcurrentDictionary<string, Delegate>(); }
            }
            _onGetHooks![propertyName] = onRead;
            return this;
        }

        public DynamicClass RemoveOnSet(string propertyName) { _onSetHooks?.TryRemove(propertyName, out _); return this; }
        public DynamicClass RemoveOnGet(string propertyName) { _onGetHooks?.TryRemove(propertyName, out _); return this; }

        public object? GetValue(string propertyName)
        {
            EnsureBuilt();
            return DynamicEntityAccessor.GetGetter<object>(_type!, propertyName)(_instance!);
        }

        public DynamicClass SetValue(string propertyName, object? value)
        {
            EnsureBuilt();
            DynamicEntityAccessor.GetSetter<object>(_type!, propertyName)(_instance!, value!);
            return this;
        }

        public Type Type { get { EnsureBuilt(); return _type!; } }
        public object RawInstance { get { EnsureBuilt(); return _instance!; } }

        public IReadOnlyList<(string Name, Type Type)> Schema { get { EnsureBuilt(); return DynamicTypeFactory.GetSchema(_type!)!; } }

        private void EnsureNotBuilt()
        {
            if (_type != null)
                throw new InvalidOperationException(
                    "[DynamicClass] Tip zaten oluşturuldu (ilk SetValue/GetValue/SetMethod/Build çağrısından " +
                    "sonra AddProperty/AddMethod çağrılamaz). Yeni bir CreateClass() ile başlayın.");
        }

        private void EnsureBuilt()
        {
            if (_type != null) return;

            lock (_buildLock)
            {
                if (_type != null) return; // double-checked locking: iki thread aynı anda ilk çağrıyı yaparsa Type/instance SADECE BİR KEZ üretilir

                Type type = _useSchemaCache
                    ? DynamicTypeFactory.CreateType(_className, _properties, _configureType, _methods.Count > 0 ? _methods : null, _genericMethods.Count > 0 ? _genericMethods : null, _events.Count > 0 ? _events : null)
                    : DynamicTypeFactory.CreateUniqueType(_className, _properties, _configureType, _methods.Count > 0 ? _methods : null, _genericMethods.Count > 0 ? _genericMethods : null, _events.Count > 0 ? _events : null);
                object instance = DynamicEntityAccessor.GetConstructor(type)();

                _instance = instance;
                _type = type; // EN SON yazılıyor (volatile) - başka bir thread _type != null gördüğünde _instance'ın da hazır olduğu garanti
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_forgetOnDispose && _type != null)
            {
                DynamicEntityAccessor.ForgetType(_type);
            }
        }
    }
}