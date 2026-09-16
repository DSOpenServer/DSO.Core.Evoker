using System;
using System.Collections.Generic;
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
    /// </summary>
    public sealed class DynamicClass : IDisposable
    {
        private readonly string _className;
        private readonly Dictionary<string, Type> _properties = new();
        private readonly Dictionary<string, Type> _methods = new(); // metot adı -> delegate tipi
        private readonly Dictionary<string, MethodInfo> _genericMethods = new(); // metot adı -> template MethodInfo (generic imzayı tanımlar)
        private readonly bool _useSchemaCache;
        private readonly bool _forgetOnDispose;

        private Type? _type;
        private object? _instance;
        private bool _disposed;

        // Hook'lar - INSTANCE'A ÖZEL (global DynamicEntityAccessor cache'ine DEĞİL).
        private Dictionary<string, Delegate>? _onSetHooks;
        private Dictionary<string, Delegate>? _onGetHooks;

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

        public DynamicClass AddProperty(string name, Type type)
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
            EnsureNotBuilt();
            _genericMethods[templateMethod.Name] = templateMethod;
            return this;
        }

        /// <summary>
        /// AddGenericMethod ile eklenmiş generic bir metoda implementasyon atar. Çağrı
        /// sözleşmesi TYPE-ERASURE'dır: implementation, metot HANGİ T ile çağrılırsa çağrılsın
        /// aynı delegate'tir - generic tip argümanları (typeArgs[0], typeArgs[1], ...) ve
        /// argüman değerleri (args[0], args[1], ...) size PARAMETRE olarak gelir, siz de
        /// sonucu object olarak (value type'sa BOX'layarak) döndürürsünüz. Bu, tek bir
        /// delegate field'ının HER ÇAĞRIDA farklı T ile çalışabilmesinin TEK yoludur.
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

        public DynamicClass Build()
        {
            EnsureBuilt();
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
            _onSetHooks ??= new Dictionary<string, Delegate>();
            _onSetHooks[propertyName] = onChanged;
            return this;
        }

        public DynamicClass OnGet<T>(string propertyName, Action<T> onRead)
        {
            _onGetHooks ??= new Dictionary<string, Delegate>();
            _onGetHooks[propertyName] = onRead;
            return this;
        }

        public DynamicClass RemoveOnSet(string propertyName) { _onSetHooks?.Remove(propertyName); return this; }
        public DynamicClass RemoveOnGet(string propertyName) { _onGetHooks?.Remove(propertyName); return this; }

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

            _type = _useSchemaCache
                ? DynamicTypeFactory.CreateType(_className, _properties, _configureType, _methods.Count > 0 ? _methods : null, _genericMethods.Count > 0 ? _genericMethods : null)
                : DynamicTypeFactory.CreateUniqueType(_className, _properties, _configureType, _methods.Count > 0 ? _methods : null, _genericMethods.Count > 0 ? _genericMethods : null);
            _instance = DynamicEntityAccessor.GetConstructor(_type)();
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