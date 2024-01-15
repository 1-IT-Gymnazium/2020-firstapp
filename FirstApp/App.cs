using System;
using System.IO;
using System.Threading;

namespace FirstApp
{
    public class App
    {
        public Random Generator { get; set; }
        public int MyProperty { get; set; }
        public int Height { get; set; } = 1000;
        public int Width { get; set; } = 1000;
        public bool Setting { get; set; }

        public One[][] ArrayOfOnes { get; set; }

        public App()
        {
            Generator = new Random();
            InitArray();

        }

        public void RunPhaseOne()
        {
            Console.WriteLine("User colors in phase two? y/n");
            var result = Console.ReadLine();
            Setting = result == "y";

            using var s = new StreamReader("./input.cs");
            string line;
            while((line = s.ReadLine()) != null)
            {
                var number = Generator.Next(1, 1000);
                if (number > 800)
                {
                    var color = Generator.Next(0, 16);
                    if(color == 0)
                        color += Generator.Next(1, 16);
                    Console.ForegroundColor = (ConsoleColor)color;
                }
                // rychlost výpisu první části
                var time = Generator.Next(0, 6);
                foreach (var item in line)
                {
                    Console.Write(item);
                    Thread.Sleep(time);
                }
                Console.WriteLine();
            }
        }

        public void RunPhaseTwo()
        {
            while (true)
            {
                Console.ResetColor();
                Console.CursorVisible = false;
                WriteToConsole();
                MoveOnes();
                Thread.Sleep(30);
                Console.Clear();
            }

        }

        private void WriteToConsole()
        {
            for (int i = 0; i < Console.WindowHeight - 1; i++)
            {
                for (int j = 0; j < Console.WindowWidth / 2; j++)
                {              
                    if(ArrayOfOnes[i][j].Value != 0 && Setting)
                    {
                        //Console.BackgroundColor = ArrayOfOnes[i][j].Background;
                        Console.ForegroundColor = ArrayOfOnes[i][j].Foreground;
                    }
                    Console.Write(ArrayOfOnes[i][j].Value == 0 ? "  " : ArrayOfOnes[i][j].Value + " ");
                    if (Setting)
                    Console.ResetColor();
                }
                Console.WriteLine();
            }
        }

        private void InitArray()
        {
            ArrayOfOnes = new One[Height][];
            for (int i = 0; i < Height; i++)
            {
                ArrayOfOnes[i] = new One[Width];
            }
            ArrayOfOnes[0] = CreateOnes(); 

            for (int i = 1; i < Height; i++)
            {
                for (int j = 0; j < Width; j++)
                {
                    ArrayOfOnes[i][j] = new One();
                }
            }
        }

        private One[] CreateOnes()
        {
            var arr = new One[Width];

            for (int i = 0; i < Width; i++)
            {
                arr[i] = new One();
                arr[i].Value = Generator.Next(0,120) < 8 ? 1 : 0;
                arr[i].Foreground = (ConsoleColor)Generator.Next(0, 16);
                arr[i].Background = (ConsoleColor)Generator.Next(0, 16);
            }

            return arr;
        }

        private void MoveOnes()
        {
            for (int i = Height-1; i > 0; i--)
            {
                for (int j = 0; j < Width; j++)
                {
                    ArrayOfOnes[i][j] = ArrayOfOnes[i-1][j];
                }
            }

            ArrayOfOnes[0] = CreateOnes();
        }
    }
}
