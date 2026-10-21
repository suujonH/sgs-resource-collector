using Sgs.Collector.Decode;
using Sgs.Collector.Http;

namespace Sgs.Collector.Crawl;

public sealed record RunContext(long RunId, RunProgress Progress, AesRescProvider AesResc, HeaderRuleSet Headers);
