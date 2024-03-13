# Futese
'Futese' stands for **FU**ll **TE**xt **SE**arch.

It's a simple in-memory persistable full text search engine in less than 1000 lines of C# code:

* The index part tokenizes strings and adds corresponding keys to the index
* The index tokenizer is customizable. By default it removes diacritics and stores lowercase-only
* The query part can search for phrases. It supports AND (blah blih), OR (blah | blih) and NOT (blah - blih) with its own tokenizer
* The index can be saved to a stream (or a file) and reloaded
* A thread-safe vesion using locks  (`ThreadSafeIndex<T>`) and a concurrent versions using ConcurrentDictionary (`ConcurrentIndex<T>`) are provided. `ConcurrentIndex<T>` uses much more memory.
* You can an index using a non thread-safe version, save it and load it with a thread-safe version
* The whole code is also available as a single .cs file: [Futese.cs](Amalgamation/Futese.cs)

```
// create an index with string keys
var index = new Index<string>();

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
var newIndex = new Index<string>();  // or new ThreadSafe<string>() for example
newIndex.Load(fileName);

// search again
SimpleTest(newIndex);

newIndex.KeysCount.Should().Be(index.KeysCount);
```

```
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
```
You can also use complex keys, for example this `Customer` class can be used as a key, and its data will be persisted too:

```
// a key must be IParsable and should generally implement IEquatable<T>
private sealed class Customer(int id, string firstName, string lastName, int age) :
    IParsable<Customer>, IEquatable<Customer>
{
    public int Id => id;
    public string FirstName => firstName;
    public string LastName => lastName;
    public int Age => age;

    // used when saved to a stream.
    // by default, the index will use object.ToString() to persist the key,
    // but you can also implement IStringable.ToString()
    public override string ToString() => id + "\t" + firstName + "\t" + lastName + "\t" + age;

    // use id as the real key
    public override int GetHashCode() => id.GetHashCode();
    public override bool Equals(object? obj) => Equals(obj as Customer);
    public bool Equals(Customer? other) => other != null && other.Id == id;

    // used when loading from a stream
    public static Customer Parse(string s, IFormatProvider? provider)
    {
        var split = s.Split('\t');
        return new Customer(int.Parse(split[0]), split[1], split[2], int.Parse(split[3]));
    }

    // not called by futese which expects Parse to succeed
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, [MaybeNullWhen(false)] out Customer result) => throw new NotImplementedException();
}
```
And this is how you'd use it:

```
var index = new Index<Customer>();

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
```
```
static void TestWithObjects(Index<Customer> index)
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
```
