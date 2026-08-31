using System.Buffers.Binary;

namespace DumpToolbox.Core;

/// <summary>
/// Standard reflected IEEE CRC-32 (polynomial 0xEDB88320), compatible with
/// zlib/Redump CRC-32 values. The hot Compute path uses slicing-by-8 and the
/// rolling scanner uses precomputed GF(2) shift operators.
/// </summary>
public static class Crc32
{
    private const uint Polynomial = 0xEDB88320u;
    private static readonly uint[][] Tables = BuildTables();

    public static uint Compute(ReadOnlySpan<byte> data, uint crc = 0)
    {
        crc ^= 0xFFFFFFFFu;
        int offset = 0;

        while (data.Length - offset >= 8)
        {
            uint first = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4)) ^ crc;
            uint second = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 4, 4));

            crc = Tables[7][first & 0xFF]
                ^ Tables[6][(first >> 8) & 0xFF]
                ^ Tables[5][(first >> 16) & 0xFF]
                ^ Tables[4][first >> 24]
                ^ Tables[3][second & 0xFF]
                ^ Tables[2][(second >> 8) & 0xFF]
                ^ Tables[1][(second >> 16) & 0xFF]
                ^ Tables[0][second >> 24];

            offset += 8;
        }

        while (offset < data.Length)
            crc = Tables[0][(crc ^ data[offset++]) & 0xFF] ^ (crc >> 8);

        return crc ^ 0xFFFFFFFFu;
    }

    /// <summary>Creates a reusable operator equivalent to appending byteCount zero bytes.</summary>
    public static ShiftOperator CreateShiftOperator(long byteCount)
    {
        if (byteCount < 0)
            throw new ArgumentOutOfRangeException(nameof(byteCount));

        var basis = new uint[32];
        if (byteCount == 0)
        {
            for (int i = 0; i < 32; i++)
                basis[i] = 1u << i;
            return new ShiftOperator(basis);
        }

        // This is done only during scanner setup, never in the hot loop.
        for (int i = 0; i < 32; i++)
            basis[i] = ShiftSlow(1u << i, byteCount);

        return new ShiftOperator(basis);
    }

    /// <summary>
    /// Creates the inverse of the operator that appends <paramref name="byteCount"/> zero bytes.
    /// This is useful for recovering the CRC of a prefix from CRC(prefix || suffix),
    /// CRC(suffix), and the suffix length.
    /// </summary>
    public static ShiftOperator CreateInverseShiftOperator(long byteCount)
    {
        ShiftOperator forward = CreateShiftOperator(byteCount);
        var leftRows = new uint[32];
        var rightRows = new uint[32];

        // Convert the forward operator's column basis to a row representation and
        // augment it with the identity matrix.
        for (int row = 0; row < 32; row++)
        {
            uint left = 0;
            for (int column = 0; column < 32; column++)
            {
                uint image = forward.Apply(1u << column);
                if (((image >> row) & 1u) != 0)
                    left |= 1u << column;
            }

            leftRows[row] = left;
            rightRows[row] = 1u << row;
        }

        // Gauss-Jordan elimination over GF(2). A CRC shift by any whole number of
        // bytes is invertible, so every column must have a pivot.
        for (int column = 0; column < 32; column++)
        {
            int pivot = column;
            while (pivot < 32 && ((leftRows[pivot] >> column) & 1u) == 0)
                pivot++;

            if (pivot == 32)
                throw new InvalidOperationException("CRC32 shift operator is not invertible.");

            if (pivot != column)
            {
                (leftRows[column], leftRows[pivot]) = (leftRows[pivot], leftRows[column]);
                (rightRows[column], rightRows[pivot]) = (rightRows[pivot], rightRows[column]);
            }

            for (int row = 0; row < 32; row++)
            {
                if (row == column || ((leftRows[row] >> column) & 1u) == 0)
                    continue;

                leftRows[row] ^= leftRows[column];
                rightRows[row] ^= rightRows[column];
            }
        }

        // Convert inverse rows back to the column basis expected by ShiftOperator.
        var inverseBasis = new uint[32];
        for (int column = 0; column < 32; column++)
        {
            uint image = 0;
            for (int row = 0; row < 32; row++)
            {
                if (((rightRows[row] >> column) & 1u) != 0)
                    image |= 1u << row;
            }
            inverseBasis[column] = image;
        }

        return new ShiftOperator(inverseBasis);
    }

    public static uint Combine(uint crc1, uint crc2, long len2)
        => CreateShiftOperator(len2).Apply(crc1) ^ crc2;

    public readonly struct ShiftOperator
    {
        private readonly uint[] _basis;

        internal ShiftOperator(uint[] basis) => _basis = basis;

        public uint Apply(uint crc)
        {
            uint sum = 0;
            int bit = 0;
            while (crc != 0)
            {
                if ((crc & 1) != 0)
                    sum ^= _basis[bit];
                crc >>= 1;
                bit++;
            }
            return sum;
        }

        /// <summary>
        /// Builds four 256-entry tables so Apply(uint) can be evaluated with four table
        /// lookups rather than walking the set bits of the value. Intended for very hot loops.
        /// </summary>
        public uint[][] CreateByteTables()
        {
            var tables = new uint[4][];
            for (int byteIndex = 0; byteIndex < 4; byteIndex++)
            {
                tables[byteIndex] = new uint[256];
                int shift = byteIndex * 8;
                for (uint value = 0; value < 256; value++)
                    tables[byteIndex][value] = Apply(value << shift);
            }
            return tables;
        }

        public static uint ApplyByteTables(uint[][] tables, uint value)
            => tables[0][value & 0xFF]
             ^ tables[1][(value >> 8) & 0xFF]
             ^ tables[2][(value >> 16) & 0xFF]
             ^ tables[3][value >> 24];
    }

    private static uint ShiftSlow(uint crc, long byteCount)
    {
        if (byteCount <= 0)
            return crc;

        Span<uint> even = stackalloc uint[32];
        Span<uint> odd = stackalloc uint[32];

        odd[0] = Polynomial;
        uint row = 1;
        for (int n = 1; n < 32; n++)
        {
            odd[n] = row;
            row <<= 1;
        }

        MatrixSquare(even, odd);
        MatrixSquare(odd, even);

        do
        {
            MatrixSquare(even, odd);
            if ((byteCount & 1) != 0)
                crc = MatrixTimes(even, crc);
            byteCount >>= 1;

            if (byteCount == 0)
                break;

            MatrixSquare(odd, even);
            if ((byteCount & 1) != 0)
                crc = MatrixTimes(odd, crc);
            byteCount >>= 1;
        }
        while (byteCount != 0);

        return crc;
    }

    private static uint MatrixTimes(ReadOnlySpan<uint> matrix, uint vector)
    {
        uint sum = 0;
        int index = 0;
        while (vector != 0)
        {
            if ((vector & 1) != 0)
                sum ^= matrix[index];
            vector >>= 1;
            index++;
        }
        return sum;
    }

    private static void MatrixSquare(Span<uint> square, ReadOnlySpan<uint> matrix)
    {
        for (int n = 0; n < 32; n++)
            square[n] = MatrixTimes(matrix, matrix[n]);
    }

    private static uint[][] BuildTables()
    {
        var tables = new uint[8][];
        tables[0] = new uint[256];

        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? Polynomial ^ (c >> 1) : c >> 1;
            tables[0][n] = c;
        }

        for (int table = 1; table < 8; table++)
        {
            tables[table] = new uint[256];
            for (int n = 0; n < 256; n++)
            {
                uint c = tables[table - 1][n];
                tables[table][n] = tables[0][c & 0xFF] ^ (c >> 8);
            }
        }

        return tables;
    }
}
