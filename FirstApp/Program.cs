using System;

namespace FirstApp
{
    class Program
    {
        static void Main(string[] args)
        {
            var app = new App();
            app.RunPhaseOne();
            app.RunPhaseTwo();
            Console.ReadKey();
        }
    }
}
