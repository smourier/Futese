﻿/*
MIT License

Copyright (c) 2024 Simon Mourier

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/
global using global::System.Collections.Generic;
global using global::System.IO;
global using global::System.Linq;
global using global::System.Net.Http;
global using global::System.Threading.Tasks;
global using global::System.Threading;
global using global::System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Futese
{
    public class DefaultTokenizer : ITokenizer
    {
        public virtual IEnumerable<Token> EnumerateTokens(string text)
        {
            text = Preprocess(text)!;
            if (string.IsNullOrEmpty(text))
                yield break;

            var start = 0;
            var len = 0;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (BreaksToken(c))
                {
                    if (len > 0)
                    {
                        var token = BuildToken(text.Substring(start, len));
                        if (token != null)
                            yield return token;
                    }

                    start = i + 1;
                    len = 0;
                    continue;
                }
                len++;
            }

            if (len > 0)
            {
                var token = BuildToken(text.Substring(start, len));
                if (token != null)
                    yield return token;
            }
        }

        protected virtual bool BreaksToken(char c) => !char.IsAsciiLetter(c);
        protected virtual Token? BuildToken(string? text) => !string.IsNullOrEmpty(text) ? new(text) : null;
        protected virtual string? Preprocess(string? text) => RemoveDiacritics(text);

        [return: NotNullIfNotNull(nameof(text))]
        public static string? RemoveDiacritics(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return null;

            var normalized = text.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            for (var i = 0; i < normalized.Length; i++)
            {
                var uc = CharUnicodeInfo.GetUnicodeCategory(normalized[i]);
                if (uc != UnicodeCategory.NonSpacingMark)
                {
                    sb.Append(char.ToLowerInvariant(normalized[i]));
                }
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }
    }
}

namespace Futese
{
    public class Index<TKey>(ITokenizer? tokenizer = null, IEqualityComparer<TKey>? keyEqualityComparer = null) where TKey : IParsable<TKey>
    {
        private const string _fileSig = "FTS0";
        private static readonly bool _isKeyString = typeof(TKey) == typeof(string);
        private static readonly Encoding _encoding = Encoding.UTF8;
        private readonly NoKeysBranch _root = new([]);

        public ITokenizer Tokenizer { get; } = tokenizer ?? new DefaultTokenizer();
        public IEqualityComparer<TKey> KeyEqualityComparer { get; } = keyEqualityComparer ?? EqualityComparer<TKey>.Default;
        public int KeysCount { get; private set; }
        public IEnumerable<TKey> Keys => GetAllKeys(_root).Distinct();

        public void Add(TKey key) => Add(key, key is IStringable s ? s.ToString() : key.ToString()!);
        public virtual void Add(TKey key, string? text)
        {
            ArgumentNullException.ThrowIfNull(key);
            foreach (var token in Tokenizer.EnumerateTokens(text))
            {
                AddToken(key, token.Text);
            }
            KeysCount++;
        }

        public virtual void AddToken(TKey key, string token)
        {
            if (string.IsNullOrEmpty(token))
                return;

            var bytes = _encoding.GetBytes(token).AsSpan();
            Add(key, bytes, _root);
        }

        public int Remove(TKey key) => Remove([key]);
        public virtual int Remove(IEnumerable<TKey> keys)
        {
            if (keys == null)
                return 0;

            var uniqueKeys = keys.Select(k => new RemovedKey(k)).ToHashSet(new RemovedKeyEqualityComparer(KeyEqualityComparer));
            if (uniqueKeys.Count == 0)
                return 0;

            Remove(uniqueKeys, _root);
            var count = uniqueKeys.Count(k => k.Removed);
            KeysCount -= count;
            return count;
        }

        // note these methods don't guarantee distinct nor ordered results
        public IEnumerable<TKey> Search(string text, ITokenizer? tokenizer = null) => Search(new Query(text, tokenizer));
        public virtual IEnumerable<TKey> Search(Query query)
        {
            ArgumentNullException.ThrowIfNull(query);
            if (query.Tokens.Count == 0)
                yield break;

            bool allCombined()
            {
                if (query.Tokens[0] is QueryToken qt && qt.Type == QueryTokenType.Not)
                    return false;

                return query.Tokens.Skip(1).All(t => t is QueryToken qt && qt.Type == QueryTokenType.Or);
            }

            if (allCombined())
            {
                foreach (var token in query.Tokens)
                {
                    foreach (var key in SearchToken(token.Text))
                    {
                        yield return key;
                    }
                }
                yield break;
            }

            if (query.Tokens.Count == 1)
            {
                if (query.Tokens[0] is QueryToken qt) // if it's a qt, it's a Not
                {
                    var not = SearchToken(qt.Text).ToHashSet(KeyEqualityComparer);
                    foreach (var key in GetAllKeys(_root))
                    {
                        if (not.Contains(key))
                            continue;

                        yield return key;
                    }
                    yield break;
                }

                foreach (var key in SearchToken(query.Tokens[0].Text))
                {
                    yield return key;
                }
                yield break;
            }

            var set = new HashSet<TKey>(KeyEqualityComparer);
            foreach (var token in query.Tokens.Where(t => t is QueryToken qt && qt.Type == QueryTokenType.Or))
            {
                foreach (var key in SearchToken(token.Text))
                {
                    set.Add(key);
                }
            }

            var i = 0;
            foreach (var token in query.Tokens.Where(t => t is not QueryToken qt || qt.Type == QueryTokenType.And))
            {
                if (i == 0)
                {
                    if (set.Count == 0)
                    {
                        foreach (var key in SearchToken(token.Text))
                        {
                            set.Add(key);
                        }
                        continue;
                    }
                }

                i++;
                set = set.Intersect(SearchToken(token.Text)).ToHashSet(KeyEqualityComparer);
            }

            if (set.Count == 0)
                yield break;

            foreach (var token in query.Tokens.Where(t => t is QueryToken qt && qt.Type == QueryTokenType.Not))
            {
                foreach (var key in SearchToken(token.Text))
                {
                    set.Remove(key);
                }
            }

            foreach (var key in set)
            {
                yield return key;
            }
        }

        public virtual IEnumerable<TKey> SearchToken(string token)
        {
            if (string.IsNullOrEmpty(token))
                yield break;

            var bytes = _encoding.GetBytes(token);
            foreach (var key in Search(_root, bytes, 0))
            {
                yield return key;
            }
        }

        public void Load(string filePath)
        {
            ArgumentNullException.ThrowIfNull(filePath);
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Load(stream);
        }

        public virtual void Load(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);
            var sig = new byte[_fileSig.Length];
            if (stream.Read(sig, 0, 4) != sig.Length)
                throw new InvalidDataException();

            if (!sig.SequenceEqual(Encoding.ASCII.GetBytes(_fileSig)))
                throw new InvalidDataException();

            KeysCount = 0;
            using var gz = new GZipStream(stream, CompressionMode.Decompress);
            using var ms = new MemoryStream(); // otherwise it's very slow, weird when saving is very efficient
            gz.CopyTo(ms);
            ms.Position = 0;
            var reader = new BinaryReader(ms);

            // root has no token
            _ = reader.ReadInt32();
            var keys = reader.ReadInt32();
            var children = reader.ReadInt32();
            var uniqueKeys = new HashSet<TKey>(KeyEqualityComparer);
            AddChildren(reader, uniqueKeys, _root, keys, children);
            KeysCount = uniqueKeys.Count;
            GC.Collect();
        }

        public void Save(string filePath, CompressionLevel compressionLevel = CompressionLevel.Optimal)
        {
            ArgumentNullException.ThrowIfNull(filePath);
            using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            Save(stream, compressionLevel);
        }

        public virtual void Save(Stream stream, CompressionLevel compressionLevel = CompressionLevel.Optimal)
        {
            ArgumentNullException.ThrowIfNull(stream);
            stream.Write(Encoding.ASCII.GetBytes(_fileSig));
            using var gz = new GZipStream(stream, compressionLevel, true);
            var writer = new BinaryWriter(gz);
            Write(writer, _root);
        }

        private static void Remove(ISet<RemovedKey> keys, INode node)
        {
            if (node.Keys != null)
            {
                foreach (var key in keys)
                {
                    if (node.Keys.Remove(key.Key))
                    {
                        key.Removed = true;
                    }
                }
            }

            if (node.Children != null)
            {
                foreach (var kv in node.Children)
                {
                    Remove(keys, kv.Value);
                }
            }
        }

        private static void AddChildren(BinaryReader reader, HashSet<TKey> uniqueKeys, INode node, int keys, int children)
        {
            for (var i = 0; i < keys; i++)
            {
                var k = reader.ReadString();
                if (!_isKeyString)
                {
                    var key = TKey.Parse(k, CultureInfo.InvariantCulture);
                    node.Keys!.Add(key);
                    uniqueKeys.Add(key);
                }
                else
                {
                    var key = (TKey)(object)k;
                    node.Keys!.Add(key);
                    uniqueKeys.Add(key);
                }
            }

            for (var i = 0; i < children; i++)
            {
                var child = Read(reader, uniqueKeys);
                node.Children!.Add(child.Token, child);
            }
        }

        private static INode Read(BinaryReader reader, HashSet<TKey> uniqueKeys)
        {
            var tokenLength = reader.ReadInt32();
            var token = reader.ReadBytes(tokenLength);
            var keys = reader.ReadInt32();

            INode node;
            var children = reader.ReadInt32();
            if (children == 0)
            {
                node = new Leaf(token);
            }
            else if (keys == 0)
            {
                node = new NoKeysBranch(token);
            }
            else
            {
                node = new KeysBranch(token);
            }

            AddChildren(reader, uniqueKeys, node, keys, children);
            return node;
        }

        private static void Write(BinaryWriter writer, INode node)
        {
            writer.Write(node.Token.Length);
            writer.Write(node.Token);
            writer.Write(node.Keys?.Count ?? 0);
            writer.Write(node.Children?.Count ?? 0);
            if (node.Keys != null)
            {
                foreach (var key in node.Keys)
                {
                    string skey;
                    if (key is IStringable stringable)
                    {
                        skey = stringable.ToString();
                    }
                    else
                    {
                        skey = string.Format(CultureInfo.InvariantCulture, "{0}", key);
                    }
                    writer.Write(skey);
                }
            }

            if (node.Children != null)
            {
                foreach (var kv in node.Children)
                {
                    Write(writer, kv.Value);
                }
            }
        }

        private static IEnumerable<TKey> Search(INode node, byte[] bytes, int offset)
        {
            if (node.Token.Length > 0)
            {
                var match = GetLongestMatch(bytes, offset, node.Token);
                if (match == 0)
                    yield break;

                if (match == bytes.Length - offset)
                {
                    foreach (var key in GetAllKeys(node))
                    {
                        yield return key;
                    }
                    yield break;
                }

                offset += match;
            }

            if (node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    foreach (var key in Search(child.Value, bytes, offset))
                    {
                        yield return key;
                    }
                }
            }
        }

        private static IEnumerable<TKey> GetAllKeys(INode node)
        {
            if (node == null)
                yield break;

            if (node.Keys != null)
            {
                foreach (var key in node.Keys)
                {
                    yield return key;
                }
            }

            if (node.Children != null)
            {
                foreach (var kv in node.Children)
                {
                    foreach (var childKey in GetAllKeys(kv.Value))
                    {
                        yield return childKey;
                    }
                }
            }
        }

        private static void Add(TKey key, ReadOnlySpan<byte> text, NoKeysBranch branch)
        {
            var textBytes = text.ToArray();
            if (branch.Children.TryGetValue(textBytes, out var child))
            {
                if (child is NoKeysBranch nkb)
                {
                    var withKeys = new KeysBranch(nkb.Token);
                    foreach (var kv in nkb.Children)
                    {
                        withKeys.Children.Add(kv);
                    }

                    branch.Children.Remove(textBytes);
                    branch.Children[textBytes] = withKeys;

                    withKeys.Keys.Add(key);
                }
                else
                {
                    child.Keys!.Add(key);
                }
                return;
            }

            var match = GetLongestMatch(branch.Children, text);
            if (match != null)
            {
                var matchNode = match.Value.Node;
                var matchKey = matchNode.Key;
                var matchLength = match.Value.Length;
                if (matchLength == matchKey.Length)
                {
                    if (matchNode.Value is NoKeysBranch childBranch)
                    {
                        Add(key, text[matchLength..], childBranch);
                        return;
                    }

                    var newLeaf = new Leaf(text[matchLength..]);
                    newLeaf.Keys.Add(key);
                    var newBranch = new KeysBranch(matchKey);
                    foreach (var oldKey in matchNode.Value.Keys!)
                    {
                        newBranch.Keys.Add(oldKey);
                    }

                    branch.Children.Remove(matchKey);
                    branch.Children.Add(newBranch.Token, newBranch);
                    newBranch.Children.Add(newLeaf.Token, newLeaf);
                    return;
                }

                // make a split
                branch.Children.Remove(matchNode);
                var top = new NoKeysBranch(text[..matchLength]);
                branch.Children.Add(top.Token, top);

                Span<byte> keySpan = matchKey;
                INode existingChild;
                if (matchNode.Value.Children != null)
                {
                    if (matchNode.Value.Keys != null)
                    {
                        existingChild = new KeysBranch(keySpan[matchLength..]);
                    }
                    else
                    {
                        existingChild = new NoKeysBranch(keySpan[matchLength..]);
                    }

                    foreach (var oldChildren in matchNode.Value.Children)
                    {
                        existingChild.Children!.Add(oldChildren);
                    }
                }
                else
                {
                    existingChild = new Leaf(keySpan[matchLength..]);
                }

                if (matchNode.Value.Keys != null)
                {
                    foreach (var oldKey in matchNode.Value.Keys)
                    {
                        existingChild.Keys!.Add(oldKey);
                    }
                }

                top.Children.Add(existingChild.Token, existingChild);
                var newChild = new Leaf(text[matchLength..]);
                newChild.Keys.Add(key);
                top.Children.Add(newChild.Token, newChild);
                return;
            }

            // not found create a leaf
            var leaf = new Leaf(text);
            leaf.Keys.Add(key);
            branch.Children.Add(leaf.Token, leaf);
        }

        private static (KeyValuePair<byte[], INode> Node, int Length)? GetLongestMatch(IDictionary<byte[], INode> children, ReadOnlySpan<byte> text)
        {
            // search longest match in each children
            foreach (var child in children)
            {
                var match = GetLongestMatch(child.Key, 0, text);
                if (match > 0)
                    return (child, match);
            }
            return null;
        }

        private static int GetLongestMatch(byte[] bytes, int bytesOffset, ReadOnlySpan<byte> token)
        {
            var min = Math.Min(bytes.Length - bytesOffset, token.Length);
            for (var i = 0; i < min; i++)
            {
                if (bytes[i + bytesOffset] != token[i])
                    return i;
            }
            return min;
        }

        private interface INode
        {
            byte[] Token { get; }
            ICollection<TKey>? Keys { get; }
            IDictionary<byte[], INode>? Children { get; }
        }

        private class NoKeysBranch(ReadOnlySpan<byte> token) : INode
        {
            public byte[] Token { get; } = token.ToArray();
            public virtual ICollection<TKey>? Keys => null;
            public IDictionary<byte[], INode> Children { get; } = new Dictionary<byte[], INode>(ByteArrayEqualityComparer.Instance);

            public override string ToString() => _encoding.GetString(Token) + ":" + string.Join(',', Children.Select(kv => _encoding.GetString(kv.Key) + "=" + kv.Value));
        }

        private class KeysBranch(ReadOnlySpan<byte> token) : NoKeysBranch(token)
        {
            public override ICollection<TKey> Keys { get; } = [];

            public override string ToString() => base.ToString() + ":" + string.Join(',', Keys);
        }

        private sealed class Leaf(ReadOnlySpan<byte> token) : INode
        {
            public byte[] Token { get; } = token.ToArray();
            public ICollection<TKey> Keys { get; } = [];
            public IDictionary<byte[], INode>? Children => null;

            public override string ToString() => _encoding.GetString(Token) + ":" + string.Join(',', Keys);
        }

        private sealed class ByteArrayEqualityComparer : IEqualityComparer<byte[]>
        {
            public static readonly ByteArrayEqualityComparer Instance = new();
            public bool Equals(byte[]? x, byte[]? y) => x!.SequenceEqual(y!);
            public int GetHashCode([DisallowNull] byte[] bytes)
            {
                var hashCode = 0;
                foreach (var b in bytes)
                {
                    hashCode ^= b.GetHashCode();
                }
                return hashCode;
            }
        }

        private sealed class RemovedKey(TKey key)
        {
            public TKey Key = key;
            public bool Removed;
        }

        private sealed class RemovedKeyEqualityComparer(IEqualityComparer<TKey> keyEqualityComparer) : IEqualityComparer<RemovedKey>
        {
            public bool Equals(RemovedKey? x, RemovedKey? y) => keyEqualityComparer.Equals(x!.Key, y!.Key);
            public int GetHashCode([DisallowNull] RemovedKey obj) => obj.Key.GetHashCode();
        }
    }
}

namespace Futese
{
    public interface IStringable // when we don't want to use object.ToString()
    {
        string ToString();
    }
}

namespace Futese
{
    public interface ITokenizer
    {
        IEnumerable<Token> EnumerateTokens(string? text);
    }
}

namespace Futese
{
    public class Query
    {
        public Query(string text, ITokenizer? tokenizer = null)
        {
            ArgumentNullException.ThrowIfNull(text);
            OriginalText = text;
            Tokens = (tokenizer ?? new QueryDefaultTokenizer()).EnumerateTokens(OriginalText).ToArray();
        }

        public string OriginalText { get; }
        public IReadOnlyList<Token> Tokens { get; } = [];

        public override string ToString() => OriginalText;
    }
}

namespace Futese
{
    public class QueryDefaultTokenizer : DefaultTokenizer
    {
        private QueryTokenType _nextTokenType;

        private static QueryTokenType? GetNextToken(char c) => c switch
        {
            '-' => QueryTokenType.Not,
            '|' => QueryTokenType.Or,
            '+' => QueryTokenType.And,
            _ => null,
        };

        protected override Token? BuildToken(string? text)
        {
            Token? token;
            if (text?.Length > 0)
            {
                var next = GetNextToken(text[0]);
                if (next != null)
                {
                    if (text.Length == 1)
                    {
                        _nextTokenType = next.Value;
                        return null;
                    }

                    token = base.BuildToken(text[1..]);
                }
                else
                {
                    token = base.BuildToken(text!);
                }
            }
            else
            {
                token = base.BuildToken(text!);
            }

            if (token != null)
            {
                if (_nextTokenType != QueryTokenType.And)
                {
                    token = new QueryToken(token.Text, _nextTokenType);
                    _nextTokenType = QueryTokenType.And;
                }
            }
            return token;
        }

        protected override bool BreaksToken(char c)
        {
            var next = GetNextToken(c);
            if (next != null)
            {
                _nextTokenType = next.Value;
            }
            return base.BreaksToken(c);
        }
    }
}

namespace Futese
{
    public class QueryToken(string text, QueryTokenType type) : Token(text)
    {
        public virtual QueryTokenType Type { get; set; } = type;

        public override string ToString() => Type + ":" + base.ToString();
    }
}

namespace Futese
{
    public enum QueryTokenType
    {
        And, // default combination
        Or,
        Not,
    }
}

namespace Futese
{
    public class Token(string text)
    {
        public virtual string Text { get; } = text;

        public override string ToString() => Text;
    }
}


