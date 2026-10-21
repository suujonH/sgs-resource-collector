using System.IO.Compression;
using System.Text;
using Sgs.Collector.Decode;

namespace Sgs.Collector.Tests;

public sealed class PackageDecoderTests
{
    // 不在加密上下文里：sgsGame_a.sgs 是 ZIP，成员是明文
    private const string PlainKey = "web.sanguosha.com/220/h5_2/sgsGame_a.sgs";
    // 加密上下文：key 含 /res/config/
    private const string ConfigKey = "web.sanguosha.com/220/h5_2/res/config/Config_w.sgs";
    private const string GlobalConfKey = "web.sanguosha.com/220/h5_2/res/config/h5_global_conf_w.sgs";

    private const string DecryptFailed = "解密结果既不是 gzip 也不是 UTF-8 文本（解密密钥可能已变更）";
    private const string Json = "{\"id\":1}";

    private static readonly PackageDecoder Decoder = new();

    // ---------- 明文 ZIP：成员命名 ----------

    [Fact]
    public void Decode_ZipMemberWithJsonText_SuggestsJsonButKeepsTheSourcePath()
    {
        var decryptor = new RecordingDecryptor();

        var member = Assert.Single(Decoder.Decode(PlainKey, Zip(("a.sgs", Utf8(Json))), decryptor.Decrypt));

        Assert.Equal("a.sgs", member.SourcePath);
        Assert.Equal("a.json", member.SuggestedPath);
        Assert.Equal(Utf8(Json), member.Bytes);
        Assert.Null(member.Error);
        Assert.Equal(0, decryptor.Calls); // 不在加密上下文：从不解密
    }

    [Theory]
    [InlineData("a.sgs", "{\"id\":1}", "a.json")]
    [InlineData("a.sgs", "  \r\n[1,2]", "a.json")] // 前导空白不影响判断
    [InlineData("a.sgs", "syntax = \"proto3\";\nmessage A {}", "a.proto")]
    [InlineData("a.sgs", "window.x = 1;", "a.js")]
    [InlineData("a.sgs", "function f() {}", "a.js")]
    [InlineData("a.sgs", "class A {}", "a.js")]
    [InlineData("a.sgs", "var a = 1;", "a.js")]
    [InlineData("a.sgs", "let a = 1;", "a.js")]
    [InlineData("a.sgs", "const a = 1;", "a.js")]
    [InlineData("a.sgs", "!function(){return (x) => x}()", "a.js")] // 压缩过的 JS 只能靠箭头函数识别
    [InlineData("a.sgs", "just some words", "a.txt")]
    [InlineData("dir/a.SGS", "{}", "dir/a.json")] // 扩展名不区分大小写，目录保留
    [InlineData("a.dat", "{}", "a.dat")] // 不是 .sgs 的成员不改名
    public void Decode_SgsMemberExtensionFollowsTheTextContent(string name, string content, string expectedSuggested)
    {
        var member = Assert.Single(Decoder.Decode(PlainKey, Zip((name, Utf8(content))), Fail));

        Assert.Equal(name, member.SourcePath);
        Assert.Equal(expectedSuggested, member.SuggestedPath);
        Assert.Equal(Utf8(content), member.Bytes);
    }

    [Fact]
    public void Decode_SgsMemberWithBinaryContentKeepsItsName()
    {
        byte[] binary = [0xFF, 0x00, 0x10, 0x80];

        var member = Assert.Single(Decoder.Decode(PlainKey, Zip(("a.sgs", binary)), Fail));

        Assert.Equal("a.sgs", member.SuggestedPath);
        Assert.Equal(binary, member.Bytes);
        Assert.Null(member.Error);
    }

    [Fact]
    public void Decode_NestedZip_JoinsMemberPathsWithTheOuterEntryName()
    {
        var inner = Zip(("deep/b.sgs", Utf8(Json)), ("c.png", [1, 2, 3]));
        var outer = Zip(("inner.zip", inner), ("top.json", Utf8("[]")));

        var members = Decoder.Decode(PlainKey, outer, Fail);

        Assert.Equal(
            new[] { "inner.zip/deep/b.sgs", "inner.zip/c.png", "top.json" },
            members.Select(m => m.SourcePath));
        Assert.Equal(
            new[] { "inner.zip/deep/b.json", "inner.zip/c.png", "top.json" },
            members.Select(m => m.SuggestedPath));
    }

    [Fact]
    public void Decode_NormalizesBackslashSeparatorsInEntryNames()
    {
        var member = Assert.Single(Decoder.Decode(PlainKey, Zip(("dir\\sub\\a.json", Utf8(Json))), Fail));

        Assert.Equal("dir/sub/a.json", member.SourcePath);
        Assert.Equal("dir/sub/a.json", member.SuggestedPath);
    }

    [Fact]
    public void Decode_SkipsDirectoryEntries()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntry("dir/");
            using var stream = archive.CreateEntry("dir/a.json").Open();
            stream.Write(Utf8(Json));
        }

        var member = Assert.Single(Decoder.Decode(PlainKey, buffer.ToArray(), Fail));

        Assert.Equal("dir/a.json", member.SourcePath);
    }

    [Fact]
    public void Decode_GzipMembersAreDecompressed()
    {
        var zip = Zip(("a.json", Gzip(Utf8(Json))), ("b.sgs", Gzip(Utf8("window.x = 1;"))));

        var members = Decoder.Decode(PlainKey, zip, Fail);

        Assert.Equal(new[] { "a.json", "b.js" }, members.Select(m => m.SuggestedPath));
        Assert.Equal(Utf8(Json), members[0].Bytes);
        Assert.Equal(Utf8("window.x = 1;"), members[1].Bytes);
    }

    [Fact]
    public void Decode_CorruptGzipMember_FailsOnlyThatMember()
    {
        // 合法的 gzip 头（10 字节）之后是保留的 deflate 块类型（BTYPE=11），解压必定失败；
        // 单纯截断的数据不行：GZipStream 对截断的流不抛异常，只返回已解出的部分
        byte[] corrupt = [0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0x07, 0x00, 0x00];
        var zip = Zip(("good.json", Utf8(Json)), ("bad.json", corrupt), ("also-good.json", Utf8("[]")));

        var members = Decoder.Decode(PlainKey, zip, Fail);

        Assert.Equal(3, members.Count);
        Assert.Null(members[0].Error);
        Assert.Null(members[2].Error);
        Assert.Equal("bad.json", members[1].SourcePath);
        Assert.Equal("bad.json", members[1].SuggestedPath);
        Assert.Null(members[1].Bytes);
        Assert.StartsWith("InvalidDataException: ", members[1].Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_TruncatedGzipMember_FailsOnlyThatMember()
    {
        // 去掉末尾 10 字节：GZipStream 不抛异常，只靠 ISIZE 校验发现数据不完整
        var full = Gzip(Utf8("[" + string.Join(",", Enumerable.Range(0, 2000)) + "]"));
        var truncated = full[..^10];
        var zip = Zip(("good.json", Utf8(Json)), ("cut.json", truncated), ("also-good.json", Utf8("[]")));

        var members = Decoder.Decode(PlainKey, zip, Fail);

        Assert.Equal(3, members.Count);
        Assert.Null(members[0].Error);
        Assert.Null(members[2].Error);
        Assert.Null(members[1].Bytes);
        Assert.Equal("InvalidDataException: gzip 数据不完整", members[1].Error);
    }

    // ---------- 加密上下文 ----------

    [Fact]
    public void Decode_EncryptedContext_DecryptsMembersThatAreNeitherTextNorGzip()
    {
        var decryptor = new RecordingDecryptor();
        var zip = Zip(
            ("a.sgs", Encrypt(Utf8(Json))),
            ("b.sgs", Encrypt(Gzip(Utf8("[1,2,3]")))), // 解密之后还是 gzip：继续解压
            ("c.sgs", Encrypt(Utf8("syntax = \"proto3\";"))));

        var members = Decoder.Decode(ConfigKey, zip, decryptor.Decrypt);

        Assert.Equal(3, decryptor.Calls);
        Assert.Equal(new[] { "a.sgs", "b.sgs", "c.sgs" }, members.Select(m => m.SourcePath));
        Assert.Equal(new[] { "a.json", "b.json", "c.proto" }, members.Select(m => m.SuggestedPath));
        Assert.Equal(Utf8(Json), members[0].Bytes);
        Assert.Equal(Utf8("[1,2,3]"), members[1].Bytes);
        Assert.All(members, m => Assert.Null(m.Error));
    }

    [Fact]
    public void Decode_EncryptedContext_LeavesTextAndGzipMembersAlone()
    {
        var decryptor = new RecordingDecryptor();
        var zip = Zip(("a.sgs", Utf8(Json)), ("b.sgs", Gzip(Utf8("[1]"))));

        var members = Decoder.Decode(ConfigKey, zip, decryptor.Decrypt);

        Assert.Equal(0, decryptor.Calls);
        Assert.Equal(new[] { "a.json", "b.json" }, members.Select(m => m.SuggestedPath));
        Assert.Equal(Utf8("[1]"), members[1].Bytes);
    }

    [Fact]
    public void Decode_PlainContext_NeverDecrypts()
    {
        var decryptor = new RecordingDecryptor();
        var ciphertext = Encrypt(Utf8(Json));

        var member = Assert.Single(Decoder.Decode(PlainKey, Zip(("a.sgs", ciphertext)), decryptor.Decrypt));

        Assert.Equal(0, decryptor.Calls);
        Assert.Equal(ciphertext, member.Bytes);
        Assert.Equal("a.sgs", member.SuggestedPath);
    }

    [Fact]
    public void Decode_DecryptedGarbage_FailsOnlyThatMember()
    {
        byte[] garbage = [0xFF, 0xFE, 0x00, 0x01];
        var decryptor = new RecordingDecryptor();
        var zip = Zip(("good.sgs", Encrypt(Utf8(Json))), ("bad.sgs", Encrypt(garbage)), ("last.sgs", Encrypt(Utf8("[]"))));

        var members = Decoder.Decode(ConfigKey, zip, decryptor.Decrypt);

        Assert.Equal(3, members.Count);
        Assert.Equal("good.json", members[0].SuggestedPath);
        Assert.Equal("last.json", members[2].SuggestedPath);
        Assert.Null(members[0].Error);
        Assert.Null(members[2].Error);

        var bad = members[1];
        Assert.Equal("bad.sgs", bad.SourcePath);
        Assert.Equal("bad.sgs", bad.SuggestedPath);
        Assert.Null(bad.Bytes);
        Assert.Equal(DecryptFailed, bad.Error);
    }

    [Fact]
    public void Decode_DecryptThrows_FailsOnlyTheMembersThatNeededIt()
    {
        var zip = Zip(("plain.json", Utf8(Json)), ("secret.sgs", Encrypt(Utf8("[]"))), ("zipped.sgs", Gzip(Utf8("[]"))));

        var members = Decoder.Decode(ConfigKey, zip, _ => throw new InvalidOperationException("boom"));

        Assert.Equal(3, members.Count);
        Assert.Null(members[0].Error);
        Assert.Null(members[2].Error);
        Assert.Equal("secret.sgs", members[1].SourcePath);
        Assert.Equal("secret.sgs", members[1].SuggestedPath);
        Assert.Null(members[1].Bytes);
        Assert.Equal("InvalidOperationException: boom", members[1].Error);
    }

    [Fact]
    public void Decode_AesCfbEncryptedGzipMember_IsDecryptedWithoutAesResc()
    {
        // 与 ResourceProcessor 的用法一致：现行客户端的加密方式不需要 aesresc
        using var aesResc = new AesRescProvider();
        var zip = Zip(("a.json", Utf8(Json)), ("b.sgs", CfbEncrypt(Gzip(Utf8("[1,2]")))));

        var members = Decoder.Decode(ConfigKey, zip, aesResc.Decrypt);

        Assert.Null(members[0].Error);
        Assert.Null(members[1].Error);
        Assert.Equal("b.json", members[1].SuggestedPath);
        Assert.Equal(Utf8("[1,2]"), members[1].Bytes);
    }

    [Fact]
    public void Decode_UndecryptableMemberWithoutAesResc_FailsTheMemberWithAReadableError()
    {
        using var aesResc = new AesRescProvider();
        var zip = Zip(("a.json", Utf8(Json)), ("b.sgs", Encrypt(Gzip(Utf8("[1,2]")))));

        var members = Decoder.Decode(ConfigKey, zip, aesResc.Decrypt);

        Assert.Null(members[0].Error);
        Assert.Equal(DecryptFailed, members[1].Error);
    }

    [Fact]
    public void Decode_CancellationDuringDecryptionPropagates()
    {
        var zip = Zip(("a.sgs", Encrypt(Utf8(Json))));

        Assert.Throws<OperationCanceledException>(
            () => Decoder.Decode(ConfigKey, zip, _ => throw new OperationCanceledException()));
    }

    [Theory]
    [InlineData(2047, true)] // NUL 落在前 2048 字节内：当作二进制，需要解密
    [InlineData(2048, false)] // NUL 在采样范围之外：整体仍是合法 UTF-8，当作文本
    public void Decode_TextCheckOnlyLooksForNulInTheFirst2048Bytes(int nulIndex, bool expectDecrypt)
    {
        var content = Enumerable.Repeat((byte)'a', 3000).ToArray();
        content[nulIndex] = 0;
        var decryptor = new RecordingDecryptor();

        var members = Decoder.Decode(ConfigKey, Zip(("m.sgs", content)), decryptor.Decrypt);

        Assert.Equal(expectDecrypt ? 1 : 0, decryptor.Calls);
        Assert.Equal(expectDecrypt, members[0].Error is not null); // 解密出来的是一堆 0x9E，既不是 gzip 也不是文本
        if (!expectDecrypt)
        {
            Assert.Equal(content, members[0].Bytes);
            Assert.Equal("m.txt", members[0].SuggestedPath);
        }
    }

    [Fact]
    public void Decode_TextCheckDoesNotCountControlCharacters()
    {
        // 与 StaticResourceParser.TryDecodeText（统计控制字符个数）是两套不同的判断：这里只看 NUL 和严格 UTF-8
        var content = Utf8(new string('\u0001', 20) + "text");
        var decryptor = new RecordingDecryptor();

        var members = Decoder.Decode(ConfigKey, Zip(("m.sgs", content)), decryptor.Decrypt);

        Assert.Equal(0, decryptor.Calls);
        Assert.Equal(content, members[0].Bytes);
        Assert.Equal("m.txt", members[0].SuggestedPath);
    }

    [Fact]
    public void Decode_TextCheckRejectsInvalidUtf8()
    {
        // C3 28 是非法的 UTF-8 序列，且不含 NUL：必须靠严格解码才能认出它不是文本
        byte[] notText = [0xC3, 0x28, 0x41];
        var decryptor = new RecordingDecryptor();

        Decoder.Decode(ConfigKey, Zip(("m.sgs", notText)), decryptor.Decrypt);

        Assert.Equal(1, decryptor.Calls);
    }

    // ---------- 顶层非 ZIP 的 .sgs ----------

    [Fact]
    public void Decode_EncryptedTopLevelSgs_ReturnsOneMemberNamedAfterTheFile()
    {
        var decryptor = new RecordingDecryptor();

        var member = Assert.Single(Decoder.Decode(GlobalConfKey, Encrypt(Utf8(Json)), decryptor.Decrypt));

        Assert.Equal(1, decryptor.Calls);
        Assert.Equal("h5_global_conf_w.sgs", member.SourcePath);
        Assert.Equal("h5_global_conf_w.json", member.SuggestedPath);
        Assert.Equal(Utf8(Json), member.Bytes);
        Assert.Null(member.Error);
    }

    [Fact]
    public void Decode_EncryptedTopLevelSgs_DecryptsGzipPayloadAndIgnoresTheExtensionCase()
    {
        var key = "web.sanguosha.com/220/h5_2/res/proto/Proto_w.SGS";

        var member = Assert.Single(Decoder.Decode(key, Encrypt(Gzip(Utf8("syntax = \"proto3\";"))), new RecordingDecryptor().Decrypt));

        Assert.Equal("Proto_w.SGS", member.SourcePath);
        Assert.Equal("Proto_w.proto", member.SuggestedPath);
        Assert.Equal(Utf8("syntax = \"proto3\";"), member.Bytes);
    }

    [Fact]
    public void Decode_BinaryPayloadAfterDecryption_RenamesOnlyTheTopLevelMember()
    {
        // 顶层成员无论推断出什么扩展名都会改名（.bin 也一样）；包内的 .sgs 成员只在推断出文本类扩展名时才改名
        byte[] binary = [0xFF, 0xFE, 0x00, 0x01];
        var decryptor = new RecordingDecryptor();

        var topLevel = Assert.Single(Decoder.Decode(GlobalConfKey, Encrypt(Gzip(binary)), decryptor.Decrypt));
        var packaged = Assert.Single(Decoder.Decode(ConfigKey, Zip(("a.sgs", Encrypt(Gzip(binary)))), decryptor.Decrypt));

        Assert.Equal("h5_global_conf_w.bin", topLevel.SuggestedPath);
        Assert.Equal(binary, topLevel.Bytes);
        Assert.Equal("a.sgs", packaged.SuggestedPath);
        Assert.Equal(binary, packaged.Bytes);
    }

    [Fact]
    public void Decode_EncryptedTopLevelSgs_DoesNotDecryptPlainText()
    {
        var decryptor = new RecordingDecryptor();

        var member = Assert.Single(Decoder.Decode(GlobalConfKey, Utf8(Json), decryptor.Decrypt));

        Assert.Equal(0, decryptor.Calls);
        Assert.Equal("h5_global_conf_w.json", member.SuggestedPath);
    }

    [Fact]
    public void Decode_EncryptedTopLevelSgs_WithGarbageAfterDecryption_ReturnsOneErrorMember()
    {
        byte[] garbage = [0xFF, 0xFE, 0x00, 0x01];

        var member = Assert.Single(Decoder.Decode(GlobalConfKey, Encrypt(garbage), new RecordingDecryptor().Decrypt));

        Assert.Equal("h5_global_conf_w.sgs", member.SourcePath);
        Assert.Equal("h5_global_conf_w.sgs", member.SuggestedPath);
        Assert.Null(member.Bytes);
        Assert.Equal(DecryptFailed, member.Error);
    }

    // ---------- 不是包 ----------

    [Fact]
    public void Decode_ContentThatIsNotAPackage_ReturnsAnEmptyList()
    {
        var text = Utf8(Json);

        Assert.Empty(Decoder.Decode(PlainKey, text, Fail));
        Assert.Empty(Decoder.Decode(PlainKey, [], Fail));
        Assert.Empty(Decoder.Decode("web.sanguosha.com/220/h5_2/res/a.sgs", Encrypt(text), Fail)); // .sgs，但不在加密上下文
        Assert.Empty(Decoder.Decode("web.sanguosha.com/220/h5_2/res/config/a.json", Encrypt(text), Fail)); // 加密上下文，但不是 .sgs
    }

    // ---------- 结构错误：直接抛出，由调用方把整个包记为失败 ----------

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/./b")]
    [InlineData("/abs/evil.json")]
    [InlineData("\\abs\\evil.json")]
    [InlineData("dir\\..\\evil.json")]
    public void Decode_UnsafeMemberPath_Throws(string entryName)
    {
        var zip = Zip(("ok.json", Utf8(Json)), (entryName, Utf8(Json)));

        Assert.Throws<InvalidDataException>(() => Decoder.Decode(PlainKey, zip, Fail));
    }

    [Fact]
    public void Decode_UnsafeMemberPathInsideANestedZip_Throws()
    {
        var zip = Zip(("inner.zip", Zip(("../evil", Utf8(Json)))));

        Assert.Throws<InvalidDataException>(() => Decoder.Decode(PlainKey, zip, Fail));
    }

    [Fact]
    public void Decode_CorruptZipStructure_Throws()
    {
        byte[] truncated = [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x00, 0x00];

        Assert.Throws<InvalidDataException>(() => Decoder.Decode(PlainKey, truncated, Fail));
    }

    // ---------- 辅助 ----------

    private static byte[] Fail(byte[] data) => throw new InvalidOperationException("不应该调用解密");

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    // 模拟加密：每字节异或 0xFF（自反）。明文以 ASCII 字母或括号开头时，密文首字节落在 0x80–0xBF，
    // 是孤立的 UTF-8 延续字节，严格解码一定失败，所以 PackageDecoder 会把它当作需要解密的二进制。
    // 不能用 0x5A 这类低位掩码：ASCII 异或后仍是 ASCII，会被当成文本而跳过解密
    private static byte[] Encrypt(byte[] data) => data.Select(b => (byte)(b ^ 0xFF)).ToArray();

    // 现行客户端的加密方式，对应 SgsCipher.Decrypt
    private static byte[] CfbEncrypt(byte[] data)
    {
        var padded = new byte[(data.Length / 16 + 1) * 16];
        data.CopyTo(padded, 0);
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = SgsCipher.Key;
        return aes.EncryptCfb(padded, SgsCipher.Iv, System.Security.Cryptography.PaddingMode.None, 128)[..data.Length];
    }

    private static byte[] Zip(params (string Name, byte[] Data)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                using var stream = archive.CreateEntry(name).Open();
                stream.Write(data);
            }
        }

        return buffer.ToArray();
    }

    private static byte[] Gzip(byte[] data)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(data);
        }

        return buffer.ToArray();
    }

    // 对应 AesRescProvider.Decrypt，同时记录调用次数
    private sealed class RecordingDecryptor
    {
        public int Calls { get; private set; }

        public byte[] Decrypt(byte[] data)
        {
            Calls++;
            return Encrypt(data);
        }
    }
}
