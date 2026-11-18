using Discord;
using Discord.Net;
using Discord.WebSocket;
using ScreenWatcher.Alerts;
using ScreenWatcher.Logging;

namespace ScreenWatcher.Bot;

public enum BotState { NotSetUp, Connecting, Connected, Error }

/// <summary>Bot connection state; Reason is a short Thai explanation for Connecting/Error.</summary>
public sealed record BotStatus(BotState State, string? BotName = null, string? Reason = null);

/// <summary>A text channel the bot can post in (view + send + attach files).</summary>
public sealed record ChannelChoice(ulong GuildId, string GuildName, ulong ChannelId, string ChannelName)
{
    public string Label => $"{GuildName} › #{ChannelName}";
    public override string ToString() => Label;
}

/// <summary>
/// The user's own Discord bot, running inside this app (only while the app is open). Uses
/// Discord.Net with only the non-privileged "Guilds" intent and slash commands; no message
/// content is read. The library reconnects by itself after a dropped connection.
/// </summary>
public sealed class DiscordBot : IAlertSink, IAsyncDisposable
{
    /// <summary>View Channel (1&lt;&lt;10) + Send Messages (1&lt;&lt;11) + Attach Files (1&lt;&lt;15).</summary>
    public const ulong InvitePermissions = (1UL << 10) | (1UL << 11) | (1UL << 15);

    public static string InviteUrl(ulong applicationId) =>
        $"https://discord.com/oauth2/authorize?client_id={applicationId}&permissions={InvitePermissions}&scope=bot%20applications.commands";

    readonly AppLog _log;
    readonly SsCommandHandler _ss;
    readonly MissCommandHandler? _miss;
    readonly SemaphoreSlim _connectGate = new(1, 1);
    DiscordSocketClient? _client;
    BotStatus _status = new(BotState.NotSetUp);
    ulong? _registeredGuild;
    bool _stopping;

    public DiscordBot(AppLog log, SsCommandHandler ss, MissCommandHandler? miss = null)
    {
        _log = log;
        _ss = ss;
        _miss = miss;
    }

    /// <summary>A Discord problem worth keeping in the journal (library warnings/errors, login or
    /// command registration failures). Text only, never message contents.</summary>
    public event Action<string>? Problem;

    void ReportProblem(string text)
    {
        try { Problem?.Invoke(text); } catch { }
    }

    public BotStatus Status => _status;
    public event Action<BotStatus>? StatusChanged;
    /// <summary>The list of servers/channels the bot can post in may have changed.</summary>
    public event Action? ChannelsChanged;

    /// <summary>Where alerts and /ss go (set by the UI from settings).</summary>
    public ulong? ChosenGuildId { get; set; }
    public ulong? ChosenChannelId { get; set; }

    public async Task ConnectAsync(string token)
    {
        await _connectGate.WaitAsync();
        try
        {
            await StopClientAsync();
            _stopping = false;
            _log.AddSecret(token);
            SetStatus(new(BotState.Connecting));

            var client = new DiscordSocketClient(new DiscordSocketConfig
            {
                GatewayIntents = GatewayIntents.Guilds,
                LogLevel = LogSeverity.Warning,
                AlwaysDownloadUsers = false,
            });
            client.Log += m =>
            {
                string text = $"Discord {m.Severity}: {m.Source}: {m.Message} {m.Exception?.Message}";
                _log.Write(text);
                ReportProblem(text);
                return Task.CompletedTask;
            };
            client.Ready += () => OnReadyAsync(client);
            client.Disconnected += ex =>
            {
                if (!_stopping) SetStatus(new(BotState.Connecting, _status.BotName, "กำลังเชื่อมต่อใหม่"));
                return Task.CompletedTask;
            };
            client.Connected += () => { if (_status.State != BotState.Connected && client.CurrentUser is not null) _ = OnReadyAsync(client); return Task.CompletedTask; };
            client.JoinedGuild += _ => { Changed(); return Task.CompletedTask; };
            client.LeftGuild += _ => { Changed(); return Task.CompletedTask; };
            client.ChannelCreated += _ => { Changed(); return Task.CompletedTask; };
            client.ChannelDestroyed += _ => { Changed(); return Task.CompletedTask; };
            client.ChannelUpdated += (_, _) => { Changed(); return Task.CompletedTask; };
            client.GuildUpdated += (_, _) => { Changed(); return Task.CompletedTask; };
            client.RoleUpdated += (_, _) => { Changed(); return Task.CompletedTask; };
            client.GuildMemberUpdated += (_, _) => { Changed(); return Task.CompletedTask; };
            client.SlashCommandExecuted += cmd => { _ = Task.Run(() => HandleSlashAsync(cmd)); return Task.CompletedTask; };
            _client = client;

            try
            {
                await client.LoginAsync(TokenType.Bot, token.Trim(), validateToken: true);
                await client.StartAsync();
            }
            catch (Exception ex)
            {
                _log.Write("Discord login failed", ex);
                ReportProblem($"login failed: {ex.GetType().Name}: {ex.Message}");
                SetStatus(new(BotState.Error, null, ReasonFor(ex)));
                await StopClientAsync();
            }
        }
        finally { _connectGate.Release(); }
    }

    static string ReasonFor(Exception ex) => ex switch
    {
        HttpException { HttpCode: System.Net.HttpStatusCode.Unauthorized } => "รหัสบอทไม่ถูกต้อง",
        ArgumentException => "รหัสบอทไม่ถูกต้อง",
        HttpRequestException or TimeoutException or TaskCanceledException => "ต่ออินเทอร์เน็ตไม่ได้",
        _ => "เชื่อมต่อไม่สำเร็จ",
    };

    async Task OnReadyAsync(DiscordSocketClient client)
    {
        SetStatus(new(BotState.Connected, client.CurrentUser?.Username));
        await EnsureSlashCommandAsync();
        Changed();
    }

    void Changed()
    {
        try { ChannelsChanged?.Invoke(); } catch (Exception ex) { _log.Write("ChannelsChanged handler", ex); }
    }

    /// <summary>Registers /ss (and /miss) for the chosen server only (guild commands appear immediately).</summary>
    public async Task EnsureSlashCommandAsync()
    {
        var client = _client;
        if (client is null || ChosenGuildId is not { } gid || _registeredGuild == gid) return;
        var guild = client.GetGuild(gid);
        if (guild is null) return;
        try
        {
            await guild.CreateApplicationCommandAsync(new SlashCommandBuilder()
                .WithName(SsCommandHandler.CommandName)
                .WithDescription(SsCommandHandler.CommandDescription)
                .Build());
            if (_miss is not null)
                await guild.CreateApplicationCommandAsync(new SlashCommandBuilder()
                    .WithName(MissCommandHandler.CommandName)
                    .WithDescription(MissCommandHandler.CommandDescription)
                    .AddOption(MissCommandHandler.NoteOption, ApplicationCommandOptionType.String, MissCommandHandler.NoteDescription, isRequired: false)
                    .Build());
            _registeredGuild = gid;
            _log.Write($"/ss{(_miss is null ? "" : " and /miss")} registered for server {gid}");
        }
        catch (Exception ex)
        {
            _log.Write("Could not register slash commands", ex);
            ReportProblem($"slash command registration failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    async Task HandleSlashAsync(SocketSlashCommand cmd)
    {
        try
        {
            var i = new SocketSlashAdapter(cmd);
            if (!await _ss.HandleAsync(i) && _miss is not null) await _miss.HandleAsync(i);
        }
        catch (Exception ex)
        {
            _log.Write($"/{cmd.CommandName} failed", ex);
            ReportProblem($"/{cmd.CommandName} failed: {ex.GetType().Name}");
        }
    }

    /// <summary>Text channels in every server the bot is in where it may view, send and attach files.</summary>
    public IReadOnlyList<ChannelChoice> PostableChannels()
    {
        var client = _client;
        if (client is null || _status.State != BotState.Connected) return [];
        var list = new List<ChannelChoice>();
        foreach (var g in client.Guilds.OrderBy(g => g.Name, StringComparer.CurrentCulture))
        {
            var me = g.CurrentUser;
            if (me is null) continue;
            foreach (var ch in g.TextChannels.Where(c => c is not SocketThreadChannel).OrderBy(c => c.Position))
            {
                var p = me.GetPermissions(ch);
                if (p.ViewChannel && p.SendMessages && p.AttachFiles)
                    list.Add(new ChannelChoice(g.Id, g.Name, ch.Id, ch.Name));
            }
        }
        return list;
    }

    public int GuildCount => _client?.Guilds.Count ?? 0;

    public async Task<string?> GetInviteUrlAsync()
    {
        var client = _client;
        if (client is null || _status.State != BotState.Connected) return null;
        try { return InviteUrl((await client.GetApplicationInfoAsync()).Id); }
        catch (Exception ex)
        {
            _log.Write("Could not read application id", ex);
            return client.CurrentUser is { } u ? InviteUrl(u.Id) : null;
        }
    }

    /// <summary>Sends to the chosen channel. Throws when not connected or no channel is chosen.</summary>
    public async Task SendAsync(AlertMessage m, CancellationToken ct)
    {
        var client = _client ?? throw new InvalidOperationException("Discord not set up");
        if (_status.State != BotState.Connected || client.ConnectionState != ConnectionState.Connected)
            throw new InvalidOperationException("Discord not connected");
        if (ChosenChannelId is not { } cid) throw new InvalidOperationException("No channel chosen");
        if (client.GetChannel(cid) is not IMessageChannel ch) throw new InvalidOperationException("Chosen channel not found");

        var opts = new RequestOptions { CancelToken = ct };
        if (m.Png is { Length: > 0 } png)
        {
            using var ms = new MemoryStream(png, writable: false);
            await ch.SendFileAsync(new FileAttachment(ms, m.FileName ?? "alert.png"), m.Text, options: opts);
        }
        else
        {
            await ch.SendMessageAsync(m.Text, options: opts);
        }
    }

    public async Task DisconnectAsync()
    {
        await _connectGate.WaitAsync();
        try
        {
            await StopClientAsync();
            SetStatus(new(BotState.NotSetUp));
        }
        finally { _connectGate.Release(); }
    }

    async Task StopClientAsync()
    {
        var c = _client;
        _client = null;
        _registeredGuild = null;
        if (c is null) return;
        _stopping = true;
        try { await c.StopAsync(); await c.LogoutAsync(); }
        catch (Exception ex) { _log.Write("Discord stop", ex); }
        finally { c.Dispose(); }
    }

    void SetStatus(BotStatus s)
    {
        if (s == _status) return;
        _status = s;
        try { StatusChanged?.Invoke(s); } catch (Exception ex) { _log.Write("StatusChanged handler", ex); }
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopClientAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (Exception) { }
    }

    sealed class SocketSlashAdapter(SocketSlashCommand c) : ISlashInteraction
    {
        public string CommandName => c.CommandName;
        public ulong? ChannelId => c.ChannelId;
        public Task RespondEphemeralAsync(string text) => c.RespondAsync(text, ephemeral: true);
        public Task DeferAsync() => c.DeferAsync();
        public Task FollowupAsync(string text) => c.FollowupAsync(text);
        public string? GetString(string name) =>
            c.Data.Options.FirstOrDefault(o => o.Name == name)?.Value as string;
        public async Task FollowupWithFileAsync(byte[] png, string fileName, string text)
        {
            using var ms = new MemoryStream(png, writable: false);
            await c.FollowupWithFileAsync(new FileAttachment(ms, fileName), text);
        }
    }
}
