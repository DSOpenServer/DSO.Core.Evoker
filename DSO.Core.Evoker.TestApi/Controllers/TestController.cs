using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace DSO.Core.Evoker.TestApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class TestController : ControllerBase
    {
        [HttpGet("Test1")]
        public void Test1()
        {
            var run = EvokerEngine.InvokePublic<TestClass1,string>("Run", 5, "text alanı");

            var run2 = EvokerEngine.InvokePublic<TestClass1,int>("Run2");
        }

        [HttpGet("Test2Contractor")]
        public void Test2Contractor()
        {
            var run2 = EvokerEngine.Target<TestClass2>("Contractor stringi", 99)
                .Invoke<string>("Run", 6, "altının text alanı");
        }

        [HttpGet("Test3Static")]
        public void Test3Static()
        {
            var run = EvokerEngine.InvokePublic<TestClass3, string>("Run");

            var run2 = EvokerEngine.InvokePublic<TestClass3, int>("Run2");

            EvokerEngine.ExecutePublic<TestClass3>("Run3");
        }

        [HttpGet("Test4CreateDynamicClass")]
        public void Test4CreateDynamicClass()
        {
            // 3. Emit ile Runtime'da Class Oluşturma ve Property Set/Get Testi
            var props = new Dictionary<string, Type>
            {
                { "Id", typeof(int) },
                { "Name", typeof(string) }
            };

            Type dynamicType = DynamicTypeFactory.CreateType("DynamicCustomer", props);
            object instance = Activator.CreateInstance(dynamicType)!;

            // Property değerini atıyoruz
            dynamicType.GetProperty("Name")?.SetValue(instance, "Dokuz Sistem");

            // EvokerBuilder'a 'SetInstance' ile hazırladığımız nesneyi geçiyoruz
            var nameValue = new EvokerBuilder(dynamicType)
                .SetInstance(instance)
                .Invoke<string>("get_Name");

            Console.WriteLine($"Dinamik Class Property Değeri: {nameValue}");
            // Çıktı: Dinamik Class Property Değeri: Dokuz Sistem
        }
    }
}
