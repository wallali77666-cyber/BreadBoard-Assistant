// ============================================================================
// FILE: src/4_BreadboardAOI.Intelligence/SentenceStreamSplitter.cs
// PURPOSE: Re-assembles token fragments from llama-server into complete
//          sentences so Kokoro/SAPI can speak one natural utterance at a time.
//          Decimal points ("3.3V") and dotted abbreviations never split.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace BreadboardAOI.Intelligence;

public sealed class SentenceStreamSplitter
{
    private readonly StringBuilder _buffer = new();
    private readonly List<string> _ready = new();
    private readonly int _minimumSentenceLength;

    public SentenceStreamSplitter(int minimumSentenceLength = 2)
    {
        if (minimumSentenceLength < 1)
            throw new ArgumentOutOfRangeException(nameof(minimumSentenceLength));

        _minimumSentenceLength = minimumSentenceLength;
    }

    /// <summary>Characters currently held back because the sentence is not finished.</summary>
    public int PendingLength => _buffer.Length;

    /// <summary>
    /// Appends one streamed token and returns every sentence completed by it.
    /// The returned strings never include the leading whitespace between sentences.
    /// </summary>
    public IEnumerable<string> Push(string? token)
    {
        _ready.Clear();

        if (!string.IsNullOrEmpty(token))
        {
            _buffer.Append(token);
            ExtractCompletedSentences();
        }

        foreach (string sentence in _ready)
            yield return sentence;
    }

    /// <summary>Emits the trailing unterminated sentence at end of stream, if any.</summary>
    public string? Flush()
    {
        string remainder = _buffer.ToString().Trim();
        _buffer.Clear();
        return remainder.Length >= _minimumSentenceLength ? remainder : null;
    }

    /// <summary>Convenience helper used by tests and by non-streaming callers.</summary>
    public static IReadOnlyList<string> Split(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<string>();

        var splitter = new SentenceStreamSplitter();
        var sentences = new List<string>();
        foreach (string sentence in splitter.Push(text))
            sentences.Add(sentence);

        string? tail = splitter.Flush();
        if (tail is not null)
            sentences.Add(tail);

        return sentences;
    }

    /// <summary>Adapts an arbitrary token stream into sentence chunks.</summary>
    public static async IAsyncEnumerable<string> SplitAsync(
        IAsyncEnumerable<string> tokens,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        var splitter = new SentenceStreamSplitter();
        await foreach (string token in tokens.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            foreach (string sentence in splitter.Push(token))
                yield return sentence;
        }

        string? tail = splitter.Flush();
        if (tail is not null)
            yield return tail;
    }

    private void ExtractCompletedSentences()
    {
        int sentenceStart = 0;

        for (int i = 0; i < _buffer.Length; i++)
        {
            char current = _buffer[i];
            if (current is not ('.' or '!' or '?'))
                continue;

            if (current == '.' && IsDecimalPoint(i))
                continue;

            int sentenceEnd = i + 1;

            // Keep closing quotes/brackets attached to the sentence.
            while (sentenceEnd < _buffer.Length && _buffer[sentenceEnd] is '"' or '\'' or ')' or ']')
                sentenceEnd++;

            // A trailing dot after a digit might still be a decimal split across
            // tokens ("3." + "3V"), so wait for the next token instead of emitting.
            if (current == '.' && sentenceEnd >= _buffer.Length && i > 0 && char.IsDigit(_buffer[i - 1]))
                continue;

            // A real terminator must be followed by whitespace or end-of-buffer.
            if (sentenceEnd < _buffer.Length && !char.IsWhiteSpace(_buffer[sentenceEnd]))
                continue;

            string sentence = _buffer.ToString(sentenceStart, sentenceEnd - sentenceStart).Trim();
            if (sentence.Length >= _minimumSentenceLength)
                _ready.Add(sentence);

            sentenceStart = sentenceEnd;
            while (sentenceStart < _buffer.Length && char.IsWhiteSpace(_buffer[sentenceStart]))
                sentenceStart++;

            i = sentenceStart - 1;
        }

        if (sentenceStart > 0)
            _buffer.Remove(0, sentenceStart);
    }

    private bool IsDecimalPoint(int index)
    {
        if (index <= 0 || index + 1 >= _buffer.Length)
            return false;

        return char.IsDigit(_buffer[index - 1]) && char.IsDigit(_buffer[index + 1]);
    }
}
