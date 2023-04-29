using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;

namespace SubmitTool
{
    public class Program
    {
        static async Task Main() {
            Console.OutputEncoding = Encoding.UTF8;
            _dotnetPath = Environment.OSVersion.Platform == PlatformID.Unix ? "dotnet" : _dotnetPath;
            
            var program = new Program();
            await program.Initialize();
            while (true)
            {
                Console.Write("> ");
                var args = Console.ReadLine().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (args.Length == 0) continue;
                bool minimize;
                switch (args[0]) {
                    case "exit":
                        await program.Destroy();
                        Exit();
                        break;

                    case "logout":
                        program.Logout();
                        break;

                    case "get":
                        if (args.Length < 2) {
                            Console.WriteLine("Usage: get [コンテスト名] (問題数)");
                            break;
                        }

                        int number = -1;
                        if (args.Length > 2 && int.TryParse(args[2], out number)) {
                            if (number < 0 || number > 100) {
                                Console.WriteLine("問題数に正しい数値を指定してください");
                                break;
                            }
                        }

                        await program.Get(args[1], number);
                        break;

                    case "load":
                        if (args.Length != 2) {
                            Console.WriteLine("Usage: load [コンテスト名]");
                            break;
                        }

                        program.Load(args[1]);
                        break;

                    case "s":
                    case "submit":
                        string usage = $"Usage: {args[0]} (コンテスト名) [問題] (オプション)\n" +
                                       "【オプション】\n" +
                                       "--force / -F: テストケースを実行せず提出\n" +
                                       "--minimize: ソースコードを最小化する";
                        if (args.Length < 2) {
                            Console.WriteLine(usage);
                            break;
                        }

                        var options = args.Where(x => x.StartsWith("-")).ToHashSet();

                        bool force = options.Contains("--force") || options.Contains("-F");
                        minimize = options.Contains("--minimize");

                        var argsWithoutOptions = args.Where(x => !x.StartsWith("-")).ToArray();

                        if (argsWithoutOptions.Length == 1) {
                            Console.WriteLine(usage);
                            break;
                        }

                        var (contest, task) = argsWithoutOptions.Length == 2
                            ? (null, argsWithoutOptions[1])
                            : (argsWithoutOptions[1], argsWithoutOptions[2]);

                        await program.Run(contest, task, true, force, minimize);
                        break;

                    case "r":
                    case "run":
                        if (args.Length < 2) {
                            Console.WriteLine($"Usage: {args[0]} (コンテスト名) [問題]\n");
                            break;
                        }

                        (contest, task) = args.Length == 2 ? (null, args[1]) : (args[1], args[2]);

                        await program.Run(contest, task, false);
                        break;

                    case "change":
                        if (args.Length != 2) {
                            Console.WriteLine("Usage: change [言語ID]");
                            break;
                        }

                        program.Change(args[1]);
                        break;
                    case "expand":
                        minimize = args.Length > 1 && args.Skip(1).Any(a => a == "--minimize");
                        await Expand(minimize);
                        break;
                    default:
                        Console.WriteLine("無効なコマンドです");
                        break;
                }
            }
        }

        private string _contestName;
        private IReadOnlyDictionary<string, string> _tasks;
        private string _userName;
        private static readonly Regex _regex = new Regex("<input type=\"hidden\" name=\"csrf_token\" value=\"(\\S+)\" \\/>");
        private IReadOnlyDictionary<string, string> _cookies;
        private string _languageId = "4010";
        private static string _dotnetPath = "dotnet";

        public async Task Initialize()
        {
            // クッキーがあるなら取得
            if (Directory.Exists("config/cookies"))
            {
                for (int i = 0; i <= 3; i++)
                {
                    _cookies = Directory.EnumerateFiles("config/cookies")
                        .ToDictionary(file => file.Split("/\\".ToCharArray())[^1], File.ReadAllText);

                    var res = await HttpGet("https://atcoder.jp");
                    _cookies = ExtractCookies(res.Headers);

                    var str = await res.Content.ReadAsStringAsync();

                    if (!res.IsSuccessStatusCode)
                    {
                        Error("ログイン状態の取得に失敗しました。", true);
                    }

                    if (str.Contains("/register"))
                    {
                        Console.WriteLine("ログインしてください。");
                        await Login();
                        break;
                    }

                    var session = ParseSession(_cookies["REVEL_SESSION"]);
                    if (!session.ContainsKey("UserScreenName"))
                    {
                        Console.WriteLine("ログイン情報の取得に失敗しました。");
                        if (i < 3)
                            Console.WriteLine($"再試行 {i + 1} 回目");
                        else
                            Error("", true);
                        continue;
                    }
                    _userName = session["UserScreenName"];
                    Console.WriteLine("ログイン中のユーザ: " + _userName);
                    break;

                }
            }
            else
            {
                Console.WriteLine("ログインしてください。");
                await Login();
            }
        }

        public async Task Destroy()
        {
            if (!Directory.Exists("config")) Directory.CreateDirectory("config");
            if (!Directory.Exists("config/cookies")) Directory.CreateDirectory("config/cookies");
            foreach (var (key, value) in _cookies)
            {
                await File.WriteAllTextAsync($"config/cookies/{key}", value);
            }
        }
        
        public async Task Get(string contestName, int count)
        {
            var response = await HttpGet("https://atcoder.jp/contests/" + contestName);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                Error("指定されたコンテストが見つかりません。");
                return;
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                Error("コンテスト情報の取得に失敗しました。");
                return;
            }

            if (!Directory.Exists("config")) Directory.CreateDirectory("config");
            if (!Directory.Exists("config/contests")) Directory.CreateDirectory("config/contests");
            
            var list = new Dictionary<string, string>();
            var path = $"config/contests/{contestName}";

            if (count == -1)
            {
                Console.WriteLine("問題名を取得中...");
                var standings = await HttpGetString($"https://atcoder.jp/contests/{contestName}/standings/json");
                var matches = new Regex("{\"Assignment\":\"(\\w+)\",\"TaskName\":\".*?\",\"TaskScreenName\":\"(\\w+)\"}")
                    .Matches(standings);
                Console.WriteLine($"問題数: {matches.Count}問");
                foreach (Match match in matches)
                {
                    list.Add(match.Groups[1].Value.ToUpper(), match.Groups[2].Value);
                }
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    var name = $"{contestName.Replace('-', '_')}_{(char) ('a' + i)}";
                    var a = ((char) ('A' + i)).ToString();
                    list.Add(a.ToUpper(), name);
                }
                Console.WriteLine("以下の通り問題を設定しました。");
            }

            _contestName = contestName;
            _tasks = list;
            Console.WriteLine(string.Join("\n", list.Select(x => $"{x.Key}:{x.Value}")));
            await File.WriteAllLinesAsync(path, list.Select(x => $"{x.Key}\t{x.Value}"));
        }

        private void Load(string contestName)
        {
            var path = $"config/contests/{contestName}";
            if (!File.Exists(path))
            {
                Error("コンテスト情報が存在しません。");
                return;
            }
            var d = new Dictionary<string,string>();
            foreach (var line in File.ReadLines(path))
            {
                var s = line.Split('\t');
                if (s.Length != 2)
                {
                    Error("ファイルのフォーマットが誤っています。");
                    return;
                }
                d.Add(s[0].ToUpper(), s[1]);
            }

            Console.WriteLine("データをロードしました。");
            _contestName = contestName;
            _tasks = d;
        }

        private async Task Login()
        {
            const string url = "https://atcoder.jp/login";
            var response = await HttpGet(url);
            var res = await response.Content.ReadAsStringAsync();
            _cookies = ExtractCookies(response.Headers);
            var token = ExtractCsrfToken(res);
            if (res == null)
            {
                Error("csrf_tokenの取得に失敗しました。");
            }
            while (true)
            {
                var sw = Stopwatch.StartNew();
                Console.Write("ユーザ名: ");
                var username = Console.ReadLine();
                Console.Write("パスワード: ");
                var password = Console.ReadLine();
                Console.CursorTop --;
                Console.CursorLeft = 0;
                Console.WriteLine("パスワード: " + new string('*', password.Length));
                Console.WriteLine();

                if (sw.ElapsedMilliseconds < 5000)
                {
                    await Task.Delay((int)(5000 - sw.ElapsedMilliseconds));
                }
                sw.Stop();

                var param = new Dictionary<string, string>
                {
                    {"username", username},
                    {"password", password},
                    {"csrf_token", token}
                };

                HttpResponseMessage loginRes = null;
                for (int i = 0; i < 10; i++)
                {
                    if (i > 0)
                    {
                        response = await HttpGet(url);
                        res = await response.Content.ReadAsStringAsync();
                        _cookies = ExtractCookies(response.Headers);
                        token = ExtractCsrfToken(res);
                        if (token == null)
                        {
                            Error("csrf_tokenの取得に失敗しました。");
                            continue;
                        }
                    }
                    await Task.Delay(5000);
                    loginRes = await HttpPost(url, param);
                    if (loginRes.StatusCode == HttpStatusCode.OK)
                    {
                        break;
                    }

                    Console.WriteLine($"ログインに失敗しました。再試行します。 {i+1}/10");
                    await Task.Delay(100);
                }

                if (loginRes.StatusCode != HttpStatusCode.OK)
                    Error("ログインに失敗しました。", true);
                var s = await loginRes.Content.ReadAsStringAsync();
                if (s.Contains("ユーザ名またはパスワードが正しくありません。") || s.Contains("Username or Password is incorrect."))
                {
                    Error("ユーザ名またはパスワードが正しくありません。");
                    continue;
                }

                break;
            }

            Console.WriteLine("ログインしました。");
            var session = ParseSession(_cookies["REVEL_SESSION"]);
            _userName = session["UserScreenName"];
        }

        private static async Task<string> ExpandInner(string source, bool minimize)
        {
            static string Func(Match match) => match.Groups[1].Value;

            Console.WriteLine("ライブラリを展開中...");
            var libraryRegex = new Regex(@"^using Solve\.Libraries\.([\w\.]+);", RegexOptions.Multiline);
            var usingRegex = new Regex(@"^using (\S+);", RegexOptions.Multiline);
            var matches = libraryRegex.Matches(source);
            var usingSet = usingRegex.Matches(source).Select(Func).ToHashSet();
            var newUsing = new List<string>();
            var added = new HashSet<string>();

            while (matches.Any())
            {
                bool flag = false;
                foreach (Match m in matches)
                {
                    var s = Func(m);
                    if (added.Contains(s)) continue;
                    Console.WriteLine($"展開中: {s}");
                    flag = true;
                    added.Add(s);
                    var path = $"Libraries/{s.Replace('.', '/')}.cs";
                    var libSourceLines = await File.ReadAllLinesAsync(path);
                    var libSource = string.Join("\n", libSourceLines);
                    var libUsing = usingRegex.Matches(libSource).Select(Func).ToHashSet();
                    newUsing.AddRange(libUsing.Except(usingSet));
                    usingSet.UnionWith(libUsing);

                    source += string.Join(
                        "\n",
                        libSourceLines.Where(x => x.Length > 0 && !x.StartsWith("using"))
                    ) + "\n";
                }

                if (!flag) break;

                source = string.Join("\n", newUsing.Select(x => $"using {x};")) + "\n" + source;
                matches = libraryRegex.Matches(source);
                newUsing.Clear();
            }

            if (minimize)
            {
                Console.WriteLine("ソースコードの最小化中...");
                source = Regex.Replace(source, "(.*?)//.+", "$1");
                var lines = new List<string>();
                var sb = new StringBuilder();
                foreach (var s in source.Split('\n'))
                {
                    if (s.StartsWith("#"))
                    {
                        lines.Add(sb.ToString().Trim());
                        sb.Clear();
                        lines.Add(s.TrimStart(' '));
                        continue;
                    }

                    sb.Append(s.TrimStart(' ') + " ");
                }

                if (sb.Length > 0) lines.Add(sb.ToString().Trim());
                source = string.Join("\n", lines);
            }
            Console.WriteLine("展開が完了しました。");

            return source;
        }

        private static async Task Expand(bool minimize = true)
        {
            var source = string.Join("\n", await File.ReadAllLinesAsync("Solver.cs"));

            if (source.Contains("Solve.Libraries"))
            {
                await File.WriteAllTextAsync(
                    "Result.cs",
                    await ExpandInner(source, minimize)
                );
            }
            else
            {
                File.Copy("Solver.cs", "Result.cs", true);
            }
        }

        private async Task RunInner(string contestName,
            string taskName,
            bool submit = true,
            bool forceSubmit = false,
            bool minimize = true
        ) {
            
            var samples = new List<(int, string, string)>();
            {
                var task = await HttpGetString($"https://atcoder.jp/contests/{contestName}/tasks/{taskName}");
                task = task.Replace("\r\n", "\n");
                var regex = new Regex(
                    "<hr />\\n*<div class=\"part\">\\n*<section>\\n*<h3>入力例 (\\d+)</h3><pre>([\\s\\S]+?)</pre>\\n*(?:<p>[\\s\\S]+?</p>\\n*)?</section>\\n*</div>\\n*<div class=\"part\">\\n*<section>\\n*<h3>出力例 \\1</h3><pre>([\\s\\S]+?)</pre>\\n*?(?:[\\s\\S]+?\\n*)?</section>\\n*</div>",
                    RegexOptions.Multiline
                );
                var matches = regex.Matches(task);
                foreach (Match match in matches)
                {
                    samples.Add((int.Parse(match.Groups[1].Value), match.Groups[2].Value, match.Groups[3].Value));
                }
            }

            if (Directory.Exists("tmp")) Directory.Delete("tmp", true);
            Directory.CreateDirectory("tmp");

            var source = string.Join("\n", await File.ReadAllLinesAsync("Solver.cs"));

            ProcessStartInfo compilerInfo;
            
            var expand = source.Contains("Solve.Libraries");
            string compileArgument = $"publish -c Release -o {(expand ? "." : "tmp")} -v q --nologo";
            
            if (expand)
            {
                var newInfo = new ProcessStartInfo(_dotnetPath, "new console --no-restore")
                {
                    WorkingDirectory = Directory.GetCurrentDirectory() + "/tmp",
                    RedirectStandardOutput = true
                };
                var task = Process.Start(newInfo);
                source = await ExpandInner(source, minimize);
                Console.WriteLine("プロジェクトを作成中...");
                await task.WaitForExitAsync();
                await File.WriteAllTextAsync(
                    "tmp/Program.cs",
                    source
                );
                compilerInfo = new ProcessStartInfo(_dotnetPath, compileArgument)
                {
                    WorkingDirectory = $"{Environment.CurrentDirectory}/tmp",
                    RedirectStandardOutput = true
                };
            }
            else
            {
                compilerInfo = new ProcessStartInfo(_dotnetPath, compileArgument)
                {
                    WorkingDirectory = Environment.CurrentDirectory,
                    RedirectStandardOutput = true
                };
            }
            Console.WriteLine("コンパイル中...");
            var compilerProcess = Process.Start(compilerInfo);
            var compilerOutput = await compilerProcess.StandardOutput.ReadToEndAsync();
            await compilerProcess.WaitForExitAsync();
            var code = compilerProcess.ExitCode;
            if (code != 0)
            {
                Error("コンパイルエラー");
                Console.WriteLine(compilerOutput);
                return;
            }

            Console.WriteLine("コンパイル成功");

            var path = Path.Combine(Environment.CurrentDirectory, "tmp", expand ? "tmp.dll" : "AtCoder.dll");
            
            var info = new ProcessStartInfo(_dotnetPath)
            {
                Arguments = path,
                WorkingDirectory = Environment.CurrentDirectory + "/tmp",
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            static void WriteLine(string msg,
                ConsoleColor foreground = ConsoleColor.Black,
                ConsoleColor background = ConsoleColor.White
            )
            {
                var _for = Console.ForegroundColor;
                var _bac = Console.BackgroundColor;
                Console.ForegroundColor = foreground;
                Console.BackgroundColor = background;
                Console.WriteLine(msg);
                Console.ForegroundColor = _for;
                Console.BackgroundColor = _bac;
            }

            if (!forceSubmit)
            {
                var status = 0;
                foreach (var (num, input, output) in samples)
                {
                    Console.Write($"[Case {num}] ");
                    var proc = Process.Start(info);
                    var sw = Stopwatch.StartNew();
                    await proc.StandardInput.WriteLineAsync(input);
                    var ok = proc.WaitForExit(3000);
                    sw.Stop();
                    if (ok)
                    {
                        if (proc.ExitCode != 0)
                        {
                            WriteLine(" R E ", background: ConsoleColor.DarkRed);
                            Console.WriteLine(await proc.StandardError.ReadToEndAsync());
                            Console.WriteLine();
                            status = 1;
                            continue;
                        }

                        var res = await proc.StandardOutput.ReadToEndAsync();
                        var expeced = output.Split(null as char[], StringSplitOptions.RemoveEmptyEntries);
                        var result = res.Split(null as char[], StringSplitOptions.RemoveEmptyEntries);

                        bool isOk = true;
                        float? maxError = null;
                        if (expeced.Length == result.Length) {
                            for (int i = 0; i < expeced.Length; ++i) {
                                if ((expeced[i].Contains('.') || result[i].Contains('.')) &&
                                    double.TryParse(expeced[i], out var exDouble) &&
                                    double.TryParse(result[i], out var reDouble))
                                {

                                    if (double.IsNaN(reDouble)) isOk = false;

                                    var error = Math.Min(Math.Abs(reDouble - exDouble),
                                        Math.Abs(reDouble - exDouble) / exDouble);

                                    if (maxError == null || maxError < error) maxError = (float?) error;

                                    if (error > 1e-7) isOk = false;
                                } else {
                                    isOk &= expeced[i].Equals(result[i]);
                                }
                            }
                        } else {
                            isOk = false;
                        }

                        if (isOk)
                        {
                            WriteLine(" A C ", ConsoleColor.Black, ConsoleColor.Green);
                        }
                        else
                        {
                            WriteLine(" W A ", ConsoleColor.Black, ConsoleColor.Yellow);
                            status = 1;
                            var _clr = Console.ForegroundColor;
                            Console.ForegroundColor = ConsoleColor.Blue;
                            Console.WriteLine($"正解:\n{output}");
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine($"実際の出力:\n{res}");
                            Console.ForegroundColor = _clr;
                        }
                        
                        if (maxError.HasValue) {
                            Console.WriteLine($"実数の最大誤差: {maxError.Value}");
                        }

                        Console.WriteLine($"実行時間: {sw.ElapsedMilliseconds} ms");
                    }
                    else
                    {
                        WriteLine(" TLE ", ConsoleColor.White, ConsoleColor.Magenta);
                        proc.Kill();
                        status = 2;
                        break;
                    }

                    Console.WriteLine();
                }

                try {
                    Directory.Delete("tmp", true);
                } catch {
                    Error("tmp ディレクトリの削除に失敗しました。");
                }

                if (!submit) return;

                switch (status)
                {
                    case 1:
                        Console.Write("WA / RE のテストケースがあります。提出しますか？ [y/N] ");
                        break;
                    case 2:
                        Console.Write("TLE のテストケースがあります。提出しますか？ [y/N] ");
                        break;
                }

                while (status != 0)
                {
                    var key = Console.ReadKey();
                    if (Console.CursorLeft > 0) Console.CursorLeft--;
                    if (key.Key == ConsoleKey.Enter || key.Key == ConsoleKey.N)
                    {
                        Console.WriteLine();
                        return;
                    }

                    if (key.Key == ConsoleKey.Y)
                    {
                        Console.WriteLine();
                        status = 0;
                    }
                }
            }

            Console.WriteLine("提出しています...");
            string url = $"https://atcoder.jp/contests/{contestName}/submit";
            var page = await HttpGetString(url);
            var token = ExtractCsrfToken(page);
            if (token == null)
            {
                Error("csrf_tokenの取得に失敗しました。");
                return;
            }

            var post = new Dictionary<string, string>
            {
                {"data.TaskScreenName", taskName},
                {"data.LanguageId", _languageId},
                {"sourceCode", source},
                {"csrf_token", token}
            };

            var submitRes = await HttpPost(url, post);
            var submitResStr = await submitRes.Content.ReadAsStringAsync();
            if (submitRes.IsSuccessStatusCode)
            {
                var submitUrl = Regex.Match(submitResStr, "<a href=\"(.+?)\">Detail</a>");
                if (submitUrl.Success)
                {
                    Console.WriteLine("提出しました!");
                    Console.WriteLine("https://atcoder.jp" + submitUrl.Groups[1].Value);
                }
                else
                {
                    Console.WriteLine("提出のURLが取得できませんでした。提出に失敗した可能性もあります。");
                }
            }
            else
            {
                Error("提出に失敗しました。");
            }
        }

        private async Task Run(string contest, string taskName, bool submit = true, bool forceSubmit = false, bool minimize = true) {
            string name;
            if (contest is null) {
                taskName = taskName.ToUpper();
                if (_contestName == null) {
                    Error("コンテストが読み込まれていません。");
                    return;
                }

                contest = _contestName;
                if (!_tasks.ContainsKey(taskName)) {
                    Error("指定された問題は存在しません。");
                    return;
                }
                name = _tasks[taskName];
            } else {
                name = $"{contest.Replace('-', '_')}_{taskName.ToLower()}";
            }
            
            await RunInner(contest, name, submit, forceSubmit, minimize);
        }

        private void Logout()
        {
            _cookies = new Dictionary<string, string>();
            _userName = null;
            Directory.Delete("config/cookies", true);
        }

        private void Change(string name)
        {
            var next = name switch
            {
                "old" => "3006",
                "new" => "4010",
                "mcs" => "4011",
                "csc" => "4012",
                _ => null
            };

            if (next == null)
            {
                Console.WriteLine("'old', 'new', 'mcs', 'csc' のいずれかで入力してください。");
                return;
            }

            _languageId = next;
        }

        private async Task<string> HttpGetString(string url)
        {
            using var client = new HttpClient();
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            var cookies = _cookies.Select(x => $"{x.Key}={x.Value}");
            request.Headers.Add("Cookie", string.Join("; ",cookies));
            var res = await client.SendAsync(request);
            _cookies = ExtractCookies(res.Headers);
            return await res.Content.ReadAsStringAsync();
        }

        private async Task<HttpResponseMessage> HttpGet(string url)
        {
            using var client = new HttpClient();
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (_cookies != null) {
                var cookies = _cookies.Select(x => $"{x.Key}={x.Value}");
                request.Headers.Add("Cookie", string.Join("; ", cookies));
            }
            
            var res = await client.SendAsync(request);
            _cookies = ExtractCookies(res.Headers);
            return res;
        }

        private async Task<HttpResponseMessage> HttpPost(string url, Dictionary<string, string> content)
        {
            using var client = new HttpClient();
            var c = new FormUrlEncodedContent(content);
            var cookies = _cookies.Select(p => $"{p.Key}={p.Value}");
            c.Headers.Add("Cookie", string.Join(";", cookies));
            var res = await client.PostAsync(url, c);
            _cookies = ExtractCookies(res.Headers);
            return res;
        }

        private static string ExtractCsrfToken(string source)
        {
            var match = _regex.Match(source);
            return !match.Success ? null : match.Groups[1].Value;
        }

        private static IReadOnlyDictionary<string, string> ExtractCookies(HttpHeaders headers)
        {
            var res = headers.GetValues("Set-Cookie");
            return res?.Select(x =>
            {
                var s = x.Split(';')[0].Split('=');
                return new
                {
                    key = s[0],
                    value = s[1]
                };
            }).ToDictionary(x=>x.key,x=>x.value);
        }

        private static IReadOnlyDictionary<string, string> ParseSession(string session) => 
            HttpUtility.UrlDecode(session).Split("\0\0")
                .Select(x => x.Split(':'))
                .ToDictionary(x => x[0], x => x[1]);

        private static void Error(string message, bool exit = false)
        {
            var _color = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(message);

            if (exit) Environment.Exit(1);
            else Console.ForegroundColor = _color;
        }

        private static void Exit(int code = 0) => Environment.Exit(code);
    }
}
