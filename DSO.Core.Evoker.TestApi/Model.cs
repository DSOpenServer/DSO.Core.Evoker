namespace DSO.Core.Evoker.TestApi
{
    public class TestClass1
    {
        public string Run(int data, string str)
        {
            return $"Test Class Run edildi.{data.ToString()} - {str}";
        }
        public int Run2() { return 9; }
    }

    public class TestClass2
    {
        public string propString;
        public int propInt;
        public TestClass2(string prp, int prpInt)
        {
            propString = prp;
            propInt = prpInt;
        }
        public string Run(int data, string str)
        {
            return $"Test Class2 Contractor:{propString},{propInt} - Run edildi: {data.ToString()} - {str}";
        }
    }

    public class TestClass3
    {
        public static string Run()
        {
            return $"Test Class3 Run edildi. - Contractor Yok ve method yok";
        }
        public static int Run2() { return 8; }
        public static void Run3()
        {
            Console.WriteLine("Burası Run3");
        }
    }
}
