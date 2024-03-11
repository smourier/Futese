using System;
using System.Linq;
using FluentAssertions;

namespace Futese.Tests
{
    internal class Program
    {
        static void Main()
        {
            SimpleTest();
        }

        static void SimpleTest()
        {
            var index = new Index<string>();
            index.Add("a", "This is a simple phrase");
            index.Add("b", "And this one is another phrase a bit longer");
            index.Add("c", "The last phrase (this one) contains french (with diacritics) like 'réveillez-vous à l'heure!'");
            SimpleTest(index);

            var fileName = "test.fts";
            index.Save(fileName);

            var newIndex = new Index<string>();
            newIndex.Load(fileName);
            SimpleTest(newIndex);

            newIndex.KeysCount.Should().Be(index.KeysCount);
            Console.WriteLine("OK");
        }

        static void SimpleTest(Index<string> index)
        {
            string[] result;
            result = index.Search("this").Distinct().ToArray();
            result.Should().BeEquivalentTo(["a", "b", "c"]);

            result = index.Search("this is").Distinct().ToArray();
            result.Should().BeEquivalentTo(["a", "b"]);

            result = index.Search("simple | with").Distinct().ToArray();
            result.Should().BeEquivalentTo(["a", "c"]);

            result = index.Search("that").Distinct().ToArray();
            result.Should().BeEquivalentTo([]);

            result = index.Search("the").Distinct().ToArray();
            result.Should().BeEquivalentTo(["c"]);

            result = index.Search("rev").Distinct().ToArray();
            result.Should().BeEquivalentTo(["c"]);

            result = index.Search("-one").Distinct().ToArray();
            result.Should().BeEquivalentTo(["a"]);

            result = index.Search("-this | last").Distinct().ToArray();
            result.Should().BeEquivalentTo([]);
        }
    }
}
