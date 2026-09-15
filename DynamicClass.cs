using System;
using System.Collections.Generic;

namespace DSO.Core.Evoker
{
    /// <summary>
    /// AddProperty / SetValue&lt;T&gt; / GetValue&lt;T&gt; tarzı basit, akıcı (fluent) bir
    /// kullanım sağlayan sarmalayıcı. Alttan alta hâlâ DynamicTypeFactory + DynamicEntityAccessor
    /// kullanır; bu sınıf sadece "önce şema tanımla, sonra değer oku/yaz" akışını TEK bir
    /// değişken üzerinden yönetmenizi sağlar.
    ///
    /// ÖNEMLİ TASARIM KARARI (cache ömrü):
    /// Üretilen Type ve SetValue/GetValue için derlenen accessor delegate'leri bu nesnenin
    /// KENDİSİNE değil, DynamicTypeFactory/DynamicEntityAccessor'ın PAYLAŞIMLI, process-ömürlü
    /// cache'lerine aittir. "yeniClass" scope dışına çıkıp GC'lendiğinde SADECE o tek instance
    /// (alan değerlerini tutan nesne) çöpe gider; şema + derlenmiş delegate'ler cache'te KALIR.
    /// Bu BİLİNÇLİ bir tercihtir: aynı şemayla (ör. "Customer") yüzlerce/binlerce DynamicClass
    /// örneği oluşturacaksanız (tipik ORM satırı senaryosu), pahalı IL/derleme işlemi YALNIZCA
    /// İLK örnekte olur, sonrakiler her zaman ucuzdur.
    ///
    /// Şemanız GERÇEKTEN bir kerelik/tekrar etmeyecekse (ör. her çağrıda rastgele farklı kolon
    /// seti), useSchemaCache:false + forgetOnDispose:true verin ve using/Dispose ile temizleyin -
    /// aksi halde cache sessizce, sınırsız büyür.
    /// </summary>
    public sealed class DynamicClass : IDisposable
    {
        private readonly string _className;
        private readonly Dictionary<string, Type> _properties = new();
        private readonly bool _useSchemaCache;
        private readonly bool _forgetOnDispose;

        private Type? _type;
        private object? _instance;
        private bool _disposed;

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

        /// <param name="className">Görsel/debug amaçlı isim. Şema cache'i property isim+tiplerine göre çalıştığı için aynı ismi tekrar kullanmanız sorun değildir.</param>
        /// <param name="useSchemaCache">
        /// true (varsayılan, ÖNERİLEN): aynı şema tekrar istendiğinde Type YENİDEN ÜRETİLMEZ,
        /// paylaşılan cache'ten gelir. Tekrarlı/ORM kullanımı için doğru seçim.
        /// false: her zaman İZOLE, benzersiz bir Type üretilir (CreateUniqueType). Gerçekten
        /// bir kerelik/tekrar etmeyecek şemalar için; forgetOnDispose:true ile birlikte kullanın.
        /// </param>
        /// <param name="forgetOnDispose">
        /// true verilirse Dispose() çağrıldığında bu Type'a ait TÜM cache girdileri temizlenir.
        /// Sadece useSchemaCache:false ile birlikte kullanılabilir (aksi halde constructor hata fırlatır).
        /// </param>
        public static DynamicClass CreateClass(string? className = null, bool useSchemaCache = true, bool forgetOnDispose = false)
            => new DynamicClass(className ?? $"Dynamic_{Guid.NewGuid():N}", useSchemaCache, forgetOnDispose);

        public DynamicClass AddProperty(string name, Type type)
        {
            EnsureNotBuilt();
            _properties[name] = type;
            return this;
        }

        public DynamicClass AddProperty<T>(string name) => AddProperty(name, typeof(T));

        /// <summary>Şema tanımını bitirip ELLE tetiklemek isterseniz (opsiyonel - ilk SetValue/GetValue zaten otomatik tetikler).</summary>
        public DynamicClass Build()
        {
            EnsureBuilt();
            return this;
        }

        public DynamicClass SetValue<T>(string propertyName, T value)
        {
            EnsureBuilt();
            DynamicEntityAccessor.GetSetter<T>(_type!, propertyName)(_instance!, value);
            return this; // zincirleme (fluent) çağrılar için
        }

        public T GetValue<T>(string propertyName)
        {
            EnsureBuilt();
            return DynamicEntityAccessor.GetGetter<T>(_type!, propertyName)(_instance!);
        }

        public Type Type { get { EnsureBuilt(); return _type!; } }
        public object RawInstance { get { EnsureBuilt(); return _instance!; } }

        private void EnsureNotBuilt()
        {
            if (_type != null)
                throw new InvalidOperationException(
                    "[DynamicClass] Tip zaten oluşturuldu (ilk SetValue/GetValue/Build çağrısından sonra " +
                    "AddProperty çağrılamaz - Reflection.Emit tipleri bir kez CreateType() ile 'kilitlenir'). " +
                    "Yeni bir CreateClass() ile başlayın.");
        }

        private void EnsureBuilt()
        {
            if (_type != null) return;
            _type = _useSchemaCache
                ? DynamicTypeFactory.CreateType(_className, _properties)
                : DynamicTypeFactory.CreateUniqueType(_className, _properties);
            _instance = DynamicEntityAccessor.GetConstructor(_type)();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_forgetOnDispose && _type != null)
            {
                DynamicEntityAccessor.ForgetType(_type);
                // NOT: useSchemaCache:false zaten CreateUniqueType kullandığından bu Type şema
                // cache'ine hiç girmemişti - DynamicTypeFactory.ForgetSchema çağırmaya gerek yok.
            }
        }
    }
}