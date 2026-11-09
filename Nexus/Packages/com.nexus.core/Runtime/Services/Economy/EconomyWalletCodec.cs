using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Nexus.Core.Services
{
    // One versioned payload per currency: promotion must not split receipt identity
    // from its balance. Legacy numeric keys are read only until this key exists.
    internal static class EconomyWalletCodec
    {
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
        internal static string Key(string currencyId) => "NT_EcoWallet_" + currencyId;
        internal static void ValidateReceiptId(string receiptId) => StrictUtf8.GetByteCount(receiptId);

        internal static string Encode(long balance, BigDouble bigBalance, bool isBig, HashSet<string> receipts)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, StrictUtf8, leaveOpen: true);
            writer.Write((byte)1);
            writer.Write(isBig);
            if (isBig)
            {
                writer.Write(bigBalance.Mantissa);
                writer.Write(bigBalance.Exponent);
            }
            else writer.Write(balance);
            writer.Write(receipts.Count);
            foreach (string receipt in receipts) writer.Write(receipt);
            writer.Flush();
            return Convert.ToBase64String(stream.ToArray());
        }

        internal static bool TryDecode(string value, out long balance, out BigDouble bigBalance,
            out bool isBig, out HashSet<string> receipts)
        {
            balance = 0;
            bigBalance = default;
            isBig = false;
            receipts = null;
            if (string.IsNullOrEmpty(value)) return false;
            try
            {
                using var stream = new MemoryStream(Convert.FromBase64String(value), writable: false);
                using var reader = new BinaryReader(stream, StrictUtf8);
                if (reader.ReadByte() != 1) return false;
                byte mode = reader.ReadByte();
                if (mode > 1) return false;
                isBig = mode == 1;
                if (isBig)
                {
                    double mantissa = reader.ReadDouble();
                    long exponent = reader.ReadInt64();
                    if (double.IsNaN(mantissa) || double.IsInfinity(mantissa) || mantissa < 0) return false;
                    bigBalance = new BigDouble(mantissa, exponent);
                }
                else
                {
                    balance = reader.ReadInt64();
                    if (balance < 0) return false;
                }
                int count = reader.ReadInt32();
                if (count < 1 || count > stream.Length - stream.Position) return false;
                var ids = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < count; i++)
                {
                    string id = reader.ReadString();
                    if (string.IsNullOrWhiteSpace(id) || !ids.Add(id)) return false;
                }
                if (stream.Position != stream.Length) return false;
                receipts = ids;
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is FormatException || ex is ArgumentException)
            {
                return false;
            }
        }
    }
}
