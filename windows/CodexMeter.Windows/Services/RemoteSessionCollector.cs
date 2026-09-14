using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexMeter.Windows.Services;

internal sealed record RemoteCodexHost(string Id, string DisplayName, string Destination, int? Port, string? Identity);
internal sealed record RemoteSessionSnapshot(RemoteCodexHost Host, JsonObject Payload);

internal sealed class RemoteSessionCollector
{
    private const string CollectorScript = """
import datetime,json,os,pathlib,sys,time,uuid
c=__CONFIG__; days=set(c['dayKeys']); start=float(c['historyStart']); tz=c.get('timezone')
if tz:
 os.environ['TZ']=tz
 if hasattr(time,'tzset'): time.tzset()
names=dict(c.get('projectNames',{})); projects=sorted(set(c.get('projects',[])),key=len,reverse=True)
thread_projects=dict(c.get('threadProjects',{})); thread_names=dict(c.get('threadNames',{}))
def stamp(v):
 try:return datetime.datetime.fromisoformat(v.replace('Z','+00:00')) if isinstance(v,str) else None
 except:return None
def day(v):
 x=stamp(v); return x.astimezone().strftime('%Y-%m-%d') if x else None
def clean(v):
 x=' '.join(str(v).split()); return x if len(x)<=160 else x[:160].rstrip()+'…'
def user_text(p):
 ignored=('<app-context>','<skills_instructions>','<permissions instructions>','<collaboration_mode>','<apps_instructions>','<plugins_instructions>','<environment_context>')
 out=[]
 for x in p.get('content',[]):
  if isinstance(x,dict) and x.get('type') in ('input_text','text') and isinstance(x.get('text'),str) and x['text'].strip() and not x['text'].lstrip().startswith(ignored):out.append(x['text'])
 return ' '.join(out)
cache={}
def root(cwd):
 if not isinstance(cwd,str) or not cwd:return '__non_project__'
 if cwd in cache:return cache[cwd]
 p=pathlib.Path(cwd).expanduser().resolve(strict=False); raw=str(p)
 for configured in projects:
  if raw==configured or raw.startswith(configured+os.sep):cache[cwd]=configured;return configured
 while p!=p.parent:
  marker=p/'.git'
  if marker.exists():
   if marker.is_file():
    try:
     pointer=marker.read_text(encoding='utf-8',errors='ignore').replace('\\','/')
     if '/.git/worktrees/' in pointer:cache[cwd]=pointer.split('/.git/worktrees/',1)[0].split('gitdir:',1)[-1].strip();return cache[cwd]
    except OSError:pass
   cache[cwd]=str(p);return str(p)
  p=p.parent
 cache[cwd]='__non_project__';return cache[cwd]
def user_session(p):
 source=p.get('thread_source')
 return source.lower()=='user' if isinstance(source,str) and source.strip() else not isinstance(p.get('source'),dict) or p['source'].get('subagent') is None
home=pathlib.Path(os.environ.get('CODEX_HOME') or '~/.codex').expanduser(); daily={x:0 for x in days}; project_daily={}; conversations={}; contexts=[]
try:
 for line in (home/'session_index.jsonl').open(encoding='utf-8',errors='replace'):
  try:
   x=json.loads(line)
   if isinstance(x.get('id'),str) and isinstance(x.get('thread_name'),str) and x['thread_name'].strip():thread_names[x['id']]=x['thread_name'].strip()
  except:pass
except OSError:pass
try: files=(home/'sessions').rglob('*.jsonl') if (home/'sessions').is_dir() else []
except OSError: files=[]
for path in files:
 try:
  if path.stat().st_mtime<start:continue
  lines=path.open(encoding='utf-8',errors='replace')
 except OSError:continue
 tid=wid=turn=None; project='__non_project__'; include=True; previous=0; counted=False; compact=0; context=None; active=False; totals={}; auth=set(); baselines={}
 with lines:
  for line in lines:
   try:o=json.loads(line); typ=o.get('type'); p=o.get('payload'); assert isinstance(p,dict)
   except:continue
   if typ=='session_meta':
    tid=p.get('id') or tid; include=user_session(p); project=thread_projects.get(tid) or root(p.get('cwd')); w=p.get('context_window'); wid=w.get('window_id') or wid if isinstance(w,dict) else wid; continue
   if typ=='compacted':compact+=1;wid=p.get('window_id') or wid;continue
   if typ=='event_msg':
    event=p.get('type')
    if event=='task_started':
     turn=p.get('turn_id') or str(uuid.uuid4()); active=True; totals.setdefault(turn,0);baselines[turn]=previous;d=day(o.get('timestamp'))
     if include and d in daily:conversations[turn]={'turnId':turn,'threadId':tid,'contextWindowId':wid,'startedAt':o.get('timestamp'),'date':d,'projectPath':project,'projectName':names.get(project),'threadName':thread_names.get(tid),'preview':'未命名对话'}
     continue
    if event=='task_complete':active=False if (p.get('turn_id') or turn)==turn else active;continue
    if event=='user_message' and turn in conversations and isinstance(p.get('message'),str):conversations[turn]['preview']=clean(p['message']);continue
    if event!='token_count':continue
    info=p.get('info') if isinstance(p.get('info'),dict) else {}; total_usage=info.get('total_token_usage') if isinstance(info.get('total_token_usage'),dict) else {}; total=total_usage.get('total_tokens')
    if isinstance(total,(int,float)):
     total=max(0,int(total))
     if turn is not None and turn not in auth:totals[turn]=max(totals.get(turn,0),total-baselines.get(turn,previous) if total>=baselines.get(turn,previous) else total)
     delta=total-previous if total>=previous else total;previous=total
     if counted:counted=False
     else:
      d=day(o.get('timestamp')); amount=max(0,delta)
      if d in daily:daily[d]+=amount;project_daily[(d,project)]=project_daily.get((d,project),0)+amount
      if turn in conversations:conversations[turn]['tokens']=conversations[turn].get('tokens',0)+amount
    maximum=info.get('model_context_window'); last=info.get('last_token_usage') if isinstance(info.get('last_token_usage'),dict) else {}; used=last.get('total_tokens')
    if include and isinstance(maximum,(int,float)) and maximum>0 and isinstance(used,(int,float)) and stamp(o.get('timestamp')):context={'threadId':tid,'contextWindowId':wid,'projectPath':project,'projectName':names.get(project),'threadName':thread_names.get(tid),'preview':(context or {}).get('preview') or conversations.get(turn,{}).get('preview') or '未命名任务','usedTokens':max(0,int(used)),'maxTokens':int(maximum),'updatedAt':o.get('timestamp'),'compactions':compact}
    continue
   if typ=='token_usage_record':
    target=p.get('turn_id') or turn; usage=p.get('usage') if isinstance(p.get('usage'),dict) else {}; response=usage.get('total_tokens');d=day(o.get('timestamp'));counted=isinstance(response,(int,float)) and d in daily
    if counted:amount=max(0,int(response));daily[d]+=amount;project_daily[(d,project)]=project_daily.get((d,project),0)+amount
    tu=p.get('turn_token_usage') if isinstance(p.get('turn_token_usage'),dict) else {}; tt=tu.get('total_tokens')
    if target is not None and isinstance(tt,(int,float)):totals[target]=max(0,int(tt));auth.add(target)
    if target in conversations and isinstance(tt,(int,float)):conversations[target]['tokens']=max(0,int(tt))
    continue
   if typ=='response_item' and p.get('type')=='message' and p.get('role')=='user':
    meta=p.get('internal_chat_message_metadata_passthrough');target=(meta.get('turn_id') if isinstance(meta,dict) else None) or turn;text=user_text(p)
    if target in conversations and text.strip():conversations[target]['preview']=clean(text)
 if context:
  context.update({'conversationTokens':sum(totals.values()),'currentTurnId':turn,'currentTurnTokens':totals.get(turn),'currentTurnActive':active});contexts.append(context)
json.dump({'dailyTokens':daily,'conversations':list(conversations.values()),'projectDailyTokens':[{'date':k[0],'projectPath':k[1],'projectName':names.get(k[1]),'tokens':v} for k,v in project_daily.items()],'contextHealth':contexts},sys.stdout,ensure_ascii=False,separators=(',',':'))
""";

    public IReadOnlyList<RemoteCodexHost> DiscoverHosts(string codexHome)
    {
        var statePath = Path.Combine(codexHome, ".codex-global-state.json");
        try { return DiscoverHosts(JsonNode.Parse(File.ReadAllText(statePath)) as JsonObject); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
        catch (JsonException) { return []; }
    }

    internal static IReadOnlyList<RemoteCodexHost> DiscoverHosts(JsonObject? root)
    {
        if (root?["codex-managed-remote-connections"] is not JsonArray connections) return [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<RemoteCodexHost>();
        foreach (var item in connections.OfType<JsonObject>())
        {
            var id = Text(item["hostId"]);
            if (id is null || !id.StartsWith("remote-ssh-", StringComparison.Ordinal) || !seen.Add(id)) continue;
            var alias = Text(item["alias"]); var hostname = Text(item["hostname"]); var destination = alias ?? hostname;
            if (destination is null) continue;
            var port = Number(item["sshPort"]); if (port is < 1 or > 65535) port = null;
            result.Add(new RemoteCodexHost(id, Text(item["displayName"]) ?? alias ?? hostname ?? destination, destination, port, Text(item["identity"])));
        }
        return result;
    }

    public IReadOnlyList<RemoteCodexHost> ConnectedHosts(string codexHome)
    {
        var configured = DiscoverHosts(codexHome);
        if (configured.Count == 0) return [];
        var active = EstablishedSshRemoteEndpointKeys();
        if (active.Count == 0) return [];
        var selected = SelectedHostId(codexHome);
        var resolved = configured.ToDictionary(host => host.Id, host => (IReadOnlyList<string>)ResolveEndpointKeys(host).ToArray(), StringComparer.Ordinal);
        return SelectConnectedHosts(configured, selected, resolved, active);
    }

    internal static IReadOnlyList<RemoteCodexHost> SelectConnectedHosts(
        IReadOnlyList<RemoteCodexHost> configured,
        string? selectedHostId,
        IReadOnlyDictionary<string, IReadOnlyList<string>> resolvedEndpoints,
        IReadOnlySet<string> activeEndpoints)
    {
        var matches = new List<(string Endpoint, RemoteCodexHost Host)>();
        foreach (var host in configured)
        {
            var endpoint = resolvedEndpoints.GetValueOrDefault(host.Id)?.FirstOrDefault(activeEndpoints.Contains);
            if (endpoint is not null) matches.Add((endpoint, host));
        }
        return matches.GroupBy(item => item.Endpoint, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.FirstOrDefault(item => item.Host.Id == selectedHostId).Host ?? group.OrderBy(item => item.Host.Id, StringComparer.Ordinal).First().Host)
            .OrderBy(host => host.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public async Task<IReadOnlyList<RemoteSessionSnapshot>> CollectAsync(string codexHome, DateTimeOffset now, IReadOnlyList<string> dayKeys, CancellationToken cancellationToken)
    {
        var hosts = await Task.Run(() => ConnectedHosts(codexHome), cancellationToken);
        if (hosts.Count == 0) return [];
        JsonObject? state = null;
        try { state = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(codexHome, ".codex-global-state.json"), cancellationToken)) as JsonObject; } catch { }
        var tasks = hosts.Select(host => CollectOneAsync(host, BuildConfiguration(state, host, now, dayKeys), cancellationToken)).ToArray();
        var snapshots = await Task.WhenAll(tasks);
        return snapshots.OfType<RemoteSessionSnapshot>().OrderBy(item => item.Host.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static JsonObject BuildConfiguration(JsonObject? state, RemoteCodexHost host, DateTimeOffset now, IReadOnlyList<string> dayKeys)
    {
        var projectPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        var projectNames = new JsonObject();
        if (state?["remote-projects"] is JsonArray projects)
        {
            foreach (var project in projects.OfType<JsonObject>().Where(item => Text(item["hostId"]) == host.Id))
            {
                var id = Text(project["id"]); var path = Text(project["remotePath"]); if (id is null || path is null) continue;
                projectPaths[id] = path; if (Text(project["label"]) is string label) projectNames[path] = label;
            }
        }
        var threadProjects = new JsonObject(); var threadNames = new JsonObject();
        if (state?["thread-project-assignments"] is JsonObject assignments)
        {
            foreach (var assignment in assignments.Where(pair => pair.Value is JsonObject value && Text(value["hostId"]) == host.Id))
            {
                var projectId = Text(assignment.Value!["projectId"]); if (projectId is not null && projectPaths.TryGetValue(projectId, out var path)) threadProjects[assignment.Key] = path;
            }
        }
        if (state?["electron-persisted-atom-state"]?["thread-descriptions-v1"] is JsonObject descriptions)
            foreach (var pair in descriptions.Where(pair => threadProjects.ContainsKey(pair.Key))) threadNames[pair.Key] = pair.Value?.DeepClone();
        TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var iana);
        return new JsonObject
        {
            ["dayKeys"] = new JsonArray(dayKeys.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            ["historyStart"] = new DateTimeOffset(now.Date.AddDays(-89), now.Offset).ToUnixTimeSeconds(),
            ["timezone"] = iana,
            ["projects"] = new JsonArray(projectPaths.Values.Distinct(StringComparer.Ordinal).Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            ["projectNames"] = projectNames,
            ["threadProjects"] = threadProjects,
            ["threadNames"] = threadNames
        };
    }

    private static async Task<RemoteSessionSnapshot?> CollectOneAsync(RemoteCodexHost host, JsonObject config, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo { FileName = "ssh", UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        foreach (var argument in new[] { "-T", "-o", "BatchMode=yes", "-o", "ConnectionAttempts=1", "-o", "ConnectTimeout=4", "-o", "LogLevel=ERROR" }) process.StartInfo.ArgumentList.Add(argument);
        if (host.Port is int port) { process.StartInfo.ArgumentList.Add("-p"); process.StartInfo.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture)); }
        if (host.Identity is string identity) { process.StartInfo.ArgumentList.Add("-i"); process.StartInfo.ArgumentList.Add(identity); }
        process.StartInfo.ArgumentList.Add("--"); process.StartInfo.ArgumentList.Add(host.Destination); process.StartInfo.ArgumentList.Add("python3"); process.StartInfo.ArgumentList.Add("-");
        try
        {
            if (!process.Start()) return null;
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken); var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.StandardInput.WriteAsync(CollectorScript.Replace("__CONFIG__", config.ToJsonString())); await process.StandardInput.FlushAsync(); process.StandardInput.Close();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(12));
            try { await process.WaitForExitAsync(timeout.Token); } catch (OperationCanceledException) { try { process.Kill(entireProcessTree: true); } catch { } return null; }
            var output = await outputTask; _ = await errorTask;
            if (process.ExitCode != 0) return null;
            return JsonNode.Parse(output) is JsonObject payload ? new RemoteSessionSnapshot(host, payload) : null;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException) { return null; }
    }

    internal static HashSet<string> EstablishedSshRemoteEndpointKeys(string output, Func<int, bool> isSshProcess)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length < 5 || !columns[0].StartsWith("TCP", StringComparison.OrdinalIgnoreCase) ||
                !columns[^2].Equals("ESTABLISHED", StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(columns[^1], out var pid) || !isSshProcess(pid)) continue;
            var endpoint = EndpointKey(columns[2]); if (endpoint is not null) result.Add(endpoint);
        }
        return result;
    }

    private static HashSet<string> EstablishedSshRemoteEndpointKeys()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo { FileName = "netstat", Arguments = "-ano -p tcp", UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true });
            if (process is null) return [];
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(3000)) { try { process.Kill(entireProcessTree: true); } catch { } return []; }
            return EstablishedSshRemoteEndpointKeys(output, pid => { try { using var owner = Process.GetProcessById(pid); return owner.ProcessName.Equals("ssh", StringComparison.OrdinalIgnoreCase); } catch { return false; } });
        }
        catch { return []; }
    }

    private static IEnumerable<string> ResolveEndpointKeys(RemoteCodexHost host)
    {
        var hostname = host.Destination.Split('@').Last(); var port = host.Port ?? 22;
        try
        {
            using var process = new Process { StartInfo = new ProcessStartInfo { FileName = "ssh", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
            process.StartInfo.ArgumentList.Add("-G"); if (host.Port is int configuredPort) { process.StartInfo.ArgumentList.Add("-p"); process.StartInfo.ArgumentList.Add(configuredPort.ToString()); }
            if (host.Identity is string identity) { process.StartInfo.ArgumentList.Add("-i"); process.StartInfo.ArgumentList.Add(identity); }
            process.StartInfo.ArgumentList.Add("--"); process.StartInfo.ArgumentList.Add(host.Destination);
            if (process.Start())
            {
                var output = process.StandardOutput.ReadToEnd();
                var completed = process.WaitForExit(3000);
                if (!completed) { try { process.Kill(entireProcessTree: true); } catch { } }
                foreach (var line in completed ? output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>())
                {
                    var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 2) continue;
                    if (parts[0] == "hostname") hostname = parts[1]; else if (parts[0] == "port" && int.TryParse(parts[1], out var parsed)) port = parsed;
                }
            }
        }
        catch { }
        var direct = EndpointKey(hostname, port); if (direct is not null) yield return direct;
        IPAddress[] addresses; try { addresses = Dns.GetHostAddresses(hostname); } catch { yield break; }
        foreach (var address in addresses) yield return $"{address.ToString().ToLowerInvariant()}|{port}";
    }

    private static string? EndpointKey(string value, int? fallbackPort = null)
    {
        var text = value.Trim(); if (text.Length == 0) return null; text = text.Split('@').Last(); string host; int? port = fallbackPort;
        if (text.StartsWith('[') && text.IndexOf(']') is int close && close > 0) { host = text[1..close]; if (close + 2 < text.Length && int.TryParse(text[(close + 2)..], out var parsed)) port = parsed; }
        else { var colon = text.LastIndexOf(':'); if (colon > 0 && text.Count(ch => ch == ':') == 1 && int.TryParse(text[(colon + 1)..], out var parsed)) { host = text[..colon]; port = parsed; } else host = text; }
        return port is int resolved && host.Length > 0 ? $"{host.ToLowerInvariant()}|{resolved}" : null;
    }

    private static string? SelectedHostId(string codexHome) { try { return Text(JsonNode.Parse(File.ReadAllText(Path.Combine(codexHome, ".codex-global-state.json")))?["selected-remote-host-id"]); } catch { return null; } }
    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
    private static int? Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;
}
