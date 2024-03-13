using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using FluentAssertions;

namespace Futese.Tests
{
    internal class Program
    {
        static void Main()
        {
            // create an index with string keys
            RunSimpleTest(new Index<string>());
            RunSimpleTest(new ThreadSafeIndex<string>());
            RunSimpleTest(new ConcurrentIndex<string>());

            // create an index with custom keys that also contains data
            RunWithObjects(new Index<Customer>());
            RunWithObjects(new ThreadSafeIndex<Customer>());
            RunWithObjects(new ConcurrentIndex<Customer>());
            Console.WriteLine("OK");
        }

        static void RunSimpleTest(BaseIndex<string> index)
        {
            // add keys and phrases
            index.Add("a", "This is a simple phrase");
            index.Add("b", "And this one is another phrase a bit longer");
            index.Add("c", "The last phrase (this one) contains french (with diacritics) like 'réveillez-vous à l'heure!'");

            // search
            SimpleTest(index);

            // persist to a file
            var fileName = "test.fts";
            index.Save(fileName);

            // load from a file
            var newIndex = new Index<string>();
            newIndex.Load(fileName);

            // search again
            SimpleTest(newIndex);

            newIndex.KeysCount.Should().Be(index.KeysCount);

            newIndex.Remove("a");
            newIndex.KeysCount.Should().Be(index.KeysCount - 1);
            newIndex.KeysCount.Should().Be(newIndex.Keys.Count());

            newIndex.Remove(["a", "b", "c"]);
            newIndex.KeysCount.Should().Be(0);
        }

        static void SimpleTest(BaseIndex<string> index)
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

        // a key must be IParsable
        private sealed class Customer(int id, string firstName, string lastName, int age) : IParsable<Customer>, IEquatable<Customer>
        {
            public int Id => id;
            public string FirstName => firstName;
            public string LastName => lastName;
            public int Age => age;

            public override string ToString() => id + "\t" + firstName + "\t" + lastName + "\t" + age;

            // use id as the real key
            public override int GetHashCode() => id.GetHashCode();
            public override bool Equals(object? obj) => Equals(obj as Customer);
            public bool Equals(Customer? other) => other != null && other.Id == id;

            public static Customer Parse(string s, IFormatProvider? provider)
            {
                var split = s.Split('\t');
                return new Customer(int.Parse(split[0]), split[1], split[2], int.Parse(split[3]));
            }

            // not called by futese, you must always parse
            public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, [MaybeNullWhen(false)] out Customer result) => throw new NotImplementedException();
        }

        static void RunWithObjects(BaseIndex<Customer> index)
        {
            index.Add(new(0, "alice", "hunting-bobby-crown", 25));
            index.Add(new(1, "bob", "albert-down", 32));
            index.Add(new(2, "carl", "ctrl-alt", 15));

            // search
            TestWithObjects(index);

            // persist to a file
            var fileName = "customer.fts";
            index.Save(fileName);

            // load from a file
            var newIndex = new Index<Customer>();
            newIndex.Load(fileName);

            // search again
            TestWithObjects(newIndex);
        }

        static void TestWithObjects(BaseIndex<Customer> index)
        {
            Customer[] result;
            result = index.Search("al").Distinct().ToArray();
            result.Should().OnlyContain(c => c.FirstName == "alice" || c.FirstName == "bob" || c.FirstName == "carl");

            result = index.Search("b").Distinct().ToArray();
            result.Should().OnlyContain(c => c.FirstName == "alice" || c.FirstName == "bob");

            result = index.Search("a -c").Distinct().ToArray();
            result.Should().OnlyContain(c => c.FirstName == "bob");

            result = index.Search("a c").Distinct().ToArray();
            result.Should().OnlyContain(c => c.FirstName == "alice" || c.FirstName == "carl");

            result = index.Search("a d").Distinct().ToArray();
            result.Should().OnlyContain(c => c.FirstName == "bob");

            result = index.Search("hunting a").Distinct().ToArray();
            result.Should().OnlyContain(c => c.FirstName == "alice");
        }
    }
}
