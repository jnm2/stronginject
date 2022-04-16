using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;

namespace StrongInject.Generator;

internal readonly struct SourceTextBuilder
{
    private readonly FreezableSourceTextBuilder _freezableBuilder;

    public SourceTextBuilder() : this(Encoding.UTF8) // This is what SourceText.From(..., string) uses
    {
    }

    public SourceTextBuilder(Encoding? encoding)
    {
        _freezableBuilder = new(encoding);
    }

    public int Length => _freezableBuilder.Length;

    public void Append(ReadOnlySpan<char> value)
    {
        _freezableBuilder.Append(value);
    }

    public void Append(string value)
    {
        Append(value.AsSpan());
    }

    public void Append(char value)
    {
        _freezableBuilder.Append(value);
    }

    public void Append(SourceTextBuilder builder)
    {
        _freezableBuilder.Append(builder._freezableBuilder);
    }

    public void AppendLine()
    {
        Append(Environment.NewLine);
    }

    public void AppendLine(string value)
    {
        Append(value);
        Append(Environment.NewLine);
    }

    public void RemoveLast(int length)
    {
        _freezableBuilder.RemoveLast(length);
    }

    public char this[int position] => _freezableBuilder[position];

    public SourceText Build()
    {
        _freezableBuilder.Freeze();
        return _freezableBuilder;
    }

    private sealed class FreezableSourceTextBuilder : SourceText
    {
        private const int SEGMENT_LENGTH_LOG2 = 12;
        private const int SEGMENT_LENGTH = 1 << SEGMENT_LENGTH_LOG2;
        private const int SEGMENT_MASK = SEGMENT_LENGTH - 1;

        private readonly List<char[]> _segments = new();
        private int _length;
        private bool _isFrozen;

        public FreezableSourceTextBuilder(Encoding? encoding)
        {
            Encoding = encoding;
        }

        public void Append(ReadOnlySpan<char> value)
        {
            RequireMutableInstance();

            var currentSegmentRemainingLength = SEGMENT_MASK & -_length;
            _length += value.Length;

            if (currentSegmentRemainingLength > 0)
            {
                var currentSegmentRemaining = _segments[_segments.Count - 1].AsSpan(SEGMENT_LENGTH - currentSegmentRemainingLength);

                if (value.Length < currentSegmentRemainingLength)
                {
                    value.CopyTo(currentSegmentRemaining);
                    return;
                }

                value.Slice(0, currentSegmentRemainingLength).CopyTo(currentSegmentRemaining);

                value = value.Slice(currentSegmentRemainingLength);
            }

            while (value.Length > 0)
            {
                var segment = new char[SEGMENT_LENGTH];
                _segments.Add(segment);

                if (value.Length < SEGMENT_LENGTH)
                {
                    value.CopyTo(segment);
                    break;
                }

                value.Slice(0, SEGMENT_LENGTH).CopyTo(segment);
                value = value.Slice(SEGMENT_LENGTH);
            }
        }

        public void Append(char value)
        {
            RequireMutableInstance();

            var index = _length & SEGMENT_MASK;
            if (index > 0)
            {
                _segments[_length >> SEGMENT_LENGTH_LOG2][index] = value;
            }
            else
            {
                var segment = new char[SEGMENT_LENGTH];
                segment[0] = value;
                _segments.Add(segment);
            }

            _length++;
        }

        public void Append(FreezableSourceTextBuilder builder)
        {
            RequireMutableInstance();

            var currentSegmentRemainingLength = SEGMENT_MASK & -_length;
            if (currentSegmentRemainingLength == 0)
            {
                foreach (var segment in builder._segments)
                    _segments.Add((char[])segment.Clone());
            }
            else
            {
                var currentSegment = _segments[_segments.Count - 1];
                var builderLengthLeft = builder._length;

                for (var i = 0; i < builder._segments.Count; i++)
                {
                    var source = builder._segments[i];
                    Array.Copy(source, 0, currentSegment, SEGMENT_LENGTH - currentSegmentRemainingLength, Math.Min(currentSegmentRemainingLength, builderLengthLeft));

                    builderLengthLeft -= currentSegmentRemainingLength;
                    if (builderLengthLeft <= 0) break;

                    currentSegment = new char[SEGMENT_LENGTH];
                    Array.Copy(source, currentSegmentRemainingLength, currentSegment, 0, Math.Min(SEGMENT_LENGTH - currentSegmentRemainingLength, builderLengthLeft));
                    _segments.Add(currentSegment);

                    builderLengthLeft -= SEGMENT_LENGTH - currentSegmentRemainingLength;
                }
            }

            _length += builder._length;
        }

        public void RemoveLast(int length)
        {
            RequireMutableInstance();

            _length -= length;

            var neededSegmentCount = ((_length - 1) >> SEGMENT_LENGTH_LOG2) + 1;
            _segments.RemoveRange(neededSegmentCount, _segments.Count - neededSegmentCount);
        }

        private void RequireMutableInstance()
        {
            if (_isFrozen) throw new InvalidOperationException("The SourceText has been built and cannot be modified.");
        }

        public void Freeze()
        {
            _isFrozen = true;
        }

        public override char this[int position]
        {
            get
            {
                if (position < 0 || position >= _length)
                    throw new ArgumentOutOfRangeException(nameof(position));

                return _segments[position >> SEGMENT_LENGTH_LOG2][position & SEGMENT_MASK];
            }
        }

        public override Encoding? Encoding { get; }

        public override int Length => _length;

        public override void CopyTo(int sourceIndex, char[] destination, int destinationIndex, int count)
        {
            CopyTo(sourceIndex, destination.AsSpan(destinationIndex, count));
        }

        private void CopyTo(int sourceIndex, Span<char> destination)
        {
            if (sourceIndex < 0 || sourceIndex > _length)
                throw new ArgumentOutOfRangeException(nameof(sourceIndex));

            if (sourceIndex + destination.Length > _length)
                throw new ArgumentOutOfRangeException("The source index and destination length extend past the end of the source text.");

            var startingPartialSegmentLength = SEGMENT_MASK & -sourceIndex;
            if (startingPartialSegmentLength > 0)
            {
                var copyLength = Math.Min(startingPartialSegmentLength, destination.Length);

                _segments[sourceIndex >> SEGMENT_LENGTH_LOG2]
                    .AsSpan(sourceIndex & SEGMENT_MASK, copyLength)
                    .CopyTo(destination);

                sourceIndex += copyLength;
                destination = destination.Slice(copyLength);
            }

            while (destination.Length > 0)
            {
                if (destination.Length <= SEGMENT_LENGTH)
                {
                    _segments[sourceIndex >> SEGMENT_LENGTH_LOG2].AsSpan(0, destination.Length).CopyTo(destination);
                    break;
                }

                _segments[sourceIndex >> SEGMENT_LENGTH_LOG2].CopyTo(destination);
                sourceIndex += SEGMENT_LENGTH;
                destination = destination.Slice(SEGMENT_LENGTH);
            }
        }
    }
}
