using System.Buffers.Binary;
using System.Globalization;
using Qadopoolminer.Infrastructure;

namespace Qadopoolminer.Models;

public sealed class PoolMiningJob
{
    private PoolMiningJob(
        string jobId,
        string height,
        string prevHash,
        string networkTargetHex,
        string shareTargetHex,
        ulong baseTimestamp,
        string merkleRoot,
        string coinbaseAmount,
        int txCount,
        byte[] headerTemplateZeroNonce,
        string precomputedCv,
        string block1Base,
        string block2,
        string[] targetWords,
        DateTimeOffset receivedUtc)
    {
        JobId = jobId;
        Height = height;
        PrevHash = prevHash;
        NetworkTargetHex = networkTargetHex;
        ShareTargetHex = shareTargetHex;
        BaseTimestamp = baseTimestamp;
        MerkleRoot = merkleRoot;
        CoinbaseAmount = coinbaseAmount;
        TxCount = txCount;
        HeaderTemplateZeroNonce = headerTemplateZeroNonce;
        PrecomputedCv = precomputedCv;
        Block1Base = block1Base;
        Block2 = block2;
        TargetWords = targetWords;
        ReceivedUtc = receivedUtc;
        ShareTargetBytes = Convert.FromHexString(shareTargetHex);
        NetworkTargetBytes = Convert.FromHexString(networkTargetHex);
    }

    public string JobId { get; }

    public string Height { get; }

    public string PrevHash { get; }

    public string NetworkTargetHex { get; }

    public string ShareTargetHex { get; }

    public ulong BaseTimestamp { get; }

    public string MerkleRoot { get; }

    public string CoinbaseAmount { get; }

    public int TxCount { get; }

    public byte[] HeaderTemplateZeroNonce { get; }

    public string PrecomputedCv { get; }

    public string Block1Base { get; }

    public string Block2 { get; }

    public string[] TargetWords { get; }

    public DateTimeOffset ReceivedUtc { get; }

    public byte[] ShareTargetBytes { get; }

    public byte[] NetworkTargetBytes { get; }

    public string BaseTimestampText => BaseTimestamp.ToString(CultureInfo.InvariantCulture);

    public byte[] BuildHeader(ulong timestamp, ulong nonce)
    {
        var header = (byte[])HeaderTemplateZeroNonce.Clone();
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(65, 8), timestamp);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(105, 8), nonce);
        return header;
    }

    public byte[] BuildZeroNonceHeaderForTimestamp(ulong timestamp)
    {
        var header = (byte[])HeaderTemplateZeroNonce.Clone();
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(65, 8), timestamp);
        header.AsSpan(105, 8).Clear();
        return header;
    }

    public static PoolMiningJob FromApiResponse(PoolJobResponse response)
    {
        if (!ulong.TryParse(response.Timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp))
        {
            throw new InvalidOperationException("Pool job timestamp is invalid.");
        }

        var header = Convert.FromHexString(response.HeaderHexZeroNonce);
        if (header.Length != 145)
        {
            throw new InvalidOperationException("Pool job headerHexZeroNonce must be 145 bytes.");
        }

        var networkTargetHex = HexUtility.NormalizeLower(response.NetworkTarget, 32);
        var shareTargetHex = HexUtility.NormalizeLower(response.ShareTarget, 32);

        return new PoolMiningJob(
            response.JobId,
            response.Height,
            response.PrevHash,
            networkTargetHex,
            shareTargetHex,
            timestamp,
            response.MerkleRoot,
            response.CoinbaseAmount,
            response.TxCount,
            header,
            response.PrecomputedCv,
            response.Block1Base,
            response.Block2,
            response.TargetWords,
            DateTimeOffset.UtcNow);
    }
}
