# Futese
A simple in-memory persistable full text search engine in less than 800 lines of C# code:

* The index part tokenizes strings and adds corresponding keys to the index
* The index tokenizer is customizable. By default it removes diacritics and stores lowercase-only
* The query part can search for phrases. It supports AND (blah blih), OR (blah | blih) and NOT (blah - blih) with its own tokenizer
* The index can be saved to a stream (or a file) and reloaded

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
var newIndex = new Index<string>();
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
