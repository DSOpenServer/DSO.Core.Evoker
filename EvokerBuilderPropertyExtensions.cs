using System;

namespace DSO.Core.Evoker
{
    /// <summary>
    /// EvokerBuilder üzerinde property get/set için pratik köprü. DynamicEntityAccessor zaten
    /// boxing'siz, cache'li getter/setter delegate'leri sağlıyordu - ama EvokerBuilder'ı kullanan
    /// kod bunları kullanmak için ayrıca "builder.Type" + "builder.Instance" taşıyıp
    /// DynamicEntityAccessor.GetGetter/GetSetter'ı elle çağırmak zorundaydı. Bu extension'lar o
    /// iki adımı birleştirip EvokerBuilder'ın kendi API yüzeyine (Invoke/Execute/InvokeAsync ile
    /// aynı "builder üzerinden her şey" hissi) taşıyor.
    ///
    /// NOT: DSO.Core.Evoker.Plugins.Loading.EvokerBuilderDynamicInvokeExtensions'ın aksine bu dosya
    /// BİLEREK çekirdek DSO.Core.Evoker projesinde duruyor - orada "metodun dönüş şekli
    /// (void/Task/Task&lt;T&gt;) derleme zamanında bilinmiyor" sorunu plugin senaryosuna özgüydü;
    /// burada ise sadece EvokerBuilder + DynamicEntityAccessor gibi ÖNCEDEN VAR OLAN iki genel
    /// yeteneği birbirine bağlıyoruz - EvokerBuilder'ın HER kullanıcısı için faydalı, plugin'e özgü değil.
    ///
    /// KISIT: GetValue/SetValue sadece SetInstance(...) ile bağlanmış, SABİT bir nesnesi olan
    /// builder'lar için anlamlıdır. SetConstructor(...) modunda (veya hiçbir şey verilmediğinde)
    /// EvokerBuilder her Invoke/Execute çağrısında YENİ bir nesne üretir - "bu property'nin
    /// değerini oku/yaz" kavramı o modda karşılığı olmayan bir işlemdir, bu yüzden builder.Instance
    /// null ise açık bir InvalidOperationException fırlatılır.
    /// </summary>
    public static class EvokerBuilderPropertyExtensions
    {
        /// <summary>
        /// builder.Instance üzerindeki propertyName property'sinin (ya da aynı isimli field'ının)
        /// değerini, boxing'siz cache'li bir getter delegate'i (DynamicEntityAccessor.GetGetter&lt;TValue&gt;)
        /// ile okur. builder <c>includeNonPublic: true</c> ile oluşturulmuşsa (bkz. EvokerBuilder
        /// constructor'ı / builder.IncludeNonPublic) private/protected/internal property ve field'lar
        /// da görülür - Invoke/Execute'ın private metotlar için zaten yaptığı ile AYNI karar burada da
        /// geçerli, tek bir yerden (constructor) kontrol edilir.
        /// </summary>
        public static TValue GetValue<TValue>(this EvokerBuilder builder, string propertyName)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));

            var instance = RequireInstance(builder);
            var getter = DynamicEntityAccessor.GetGetter<TValue>(builder.Type, propertyName, builder.IncludeNonPublic);
            return getter(instance);
        }

        /// <summary>
        /// builder.Instance üzerindeki propertyName property'sine (ya da aynı isimli field'ına),
        /// boxing'siz cache'li bir setter delegate'i (DynamicEntityAccessor.GetSetter&lt;TValue&gt;) ile
        /// değer yazar. private/protected/internal üyeler için bkz. GetValue'daki
        /// builder.IncludeNonPublic açıklaması.
        /// </summary>
        public static void SetValue<TValue>(this EvokerBuilder builder, string propertyName, TValue value)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));

            var instance = RequireInstance(builder);
            var setter = DynamicEntityAccessor.GetSetter<TValue>(builder.Type, propertyName, builder.IncludeNonPublic);
            setter(instance, value);
        }

        /// <summary>
        /// builder.Type için DynamicEntityAccessor'da birikmiş TÜM cache girdilerini (constructor +
        /// bu builder üzerinden erişilmiş tüm property accessor'ları) temizler. Sadece bu tipin bir
        /// daha kullanılmayacağı (ör. bir plugin unload edilip DLL'i tamamen bellekten atılacaksa)
        /// senaryolarda çağırın - bkz. DynamicEntityAccessor.ForgetType'ın kendi uyarısı: paylaşımlı
        /// bir tip için çağrılırsa, o tipi kullanan başka kod yolları yeniden derleme bedelini öder.
        /// </summary>
        public static void ForgetCache(this EvokerBuilder builder)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            DynamicEntityAccessor.ForgetType(builder.Type);
            EvokerBuilder.ForgetType(builder.Type); // metot delegate'leri de (unload için şart)
        }

        private static object RequireInstance(EvokerBuilder builder)
        {
            return builder.Instance
                ?? throw new InvalidOperationException(
                    $"[EvokerBuilder] '{builder.Type.Name}' için GetValue/SetValue kullanmak üzere önce " +
                    "SetInstance(...) ile sabit bir nesne bağlamalısınız. SetConstructor(...) modunda " +
                    "(veya hiç constructor belirtilmemişken) her çağrıda YENİ bir nesne üretildiği için " +
                    "'bu property'nin değerini oku/yaz' işleminin karşılığı yoktur.");
        }
    }
}