using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using XSNotifications;
using XSNotifications.Enum;
using XSNotifications.Helpers;
using XSOverlay_VRChat_Parser.Helpers;
using XSOverlay_VRChat_Parser.Models;

namespace XSOverlay_VRChat_Parser
{
    class Program
    {
        static ConfigurationModel Configuration { get; set; }

        static HashSet<string> IgnorableAudioPaths = new HashSet<string>();
        static HashSet<string> IgnorableIconPaths = new HashSet<string>();

        public static string UserFolderPath { get; set; }
        public static string LogFileName { get; set; }
        public static class Variables
        {
            public static bool MLDetected = false;
            public static class VR
            {
                public static bool Enabled = false;
                public static string Identifier = string.Empty;
            }
                public static class User
            {
                public static string Name = string.Empty;
            }
            public static class Versions
            {
                public static Version Unity = new Version();
                public static Version CoHtml = new Version();
                public static Version OS = new Version();
                public static Version Dissonance = new Version();
            }
            public static class World
            {
                public static string Name = string.Empty;
                public static Guid UUID = new Guid();
            }
            public static IPEndPoint Server = IPEndPoint.Parse("127.0.0.1:1");
        }

        public static class Regexes
        {
            public static readonly Regex Username = new Regex(@"Successfully authenticated as: (.+)\.$", RegexOptions.Compiled);
            public static string GetUsername(string input) => Username.Match(input).Groups[1].Value;
            public static readonly Regex Server = new Regex(@"^Connected to (.*) on port (\d+) using (\w+)\.$", RegexOptions.Compiled);
            public static IPEndPoint GetServerEndpoint(string input)
            {
                var parsed = Regexes.Server.Match(input).Groups;
                return IPEndPoint.Parse(parsed[0].Value + ":" + parsed[1].Value);
            }
            public static readonly Regex UUID = new Regex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled);
            public static Guid GetUUID(string input) => Guid.Parse(UUID.Match(input).Groups[1].Value);
        }

        static readonly object logMutex = new object();

        static Timer LogDetectionTimer { get; set; }

        static Dictionary<string, TailSubscription> Subscriptions { get; set; }

        static DateTime SilencedUntil = DateTime.Now,
                        LastMaximumKeywordsNotification = DateTime.Now;

        static XSNotifier Notifier { get; set; }

        static async Task Main(string[] args)
        {
            UserFolderPath = Environment.ExpandEnvironmentVariables(@"%AppData%\..\LocalLow\XSOverlay ChilloutVR Parser");
            if (!Directory.Exists(UserFolderPath))
                Directory.CreateDirectory(UserFolderPath);

            if (!Directory.Exists($@"{UserFolderPath}\Logs"))
                Directory.CreateDirectory($@"{UserFolderPath}\Logs");

            DateTime now = DateTime.Now;
            LogFileName = $"Session_{now.Year:0000}{now.Month:00}{now.Day:00}{now.Hour:00}{now.Minute:00}{now.Second:00}.log";
            Log(LogEventType.Info, $@"Log initialized at {UserFolderPath}\Logs\{LogFileName}");

            try
            {
                if (!File.Exists($@"{UserFolderPath}\config.json"))
                {
                    Configuration = new ConfigurationModel();
                    File.WriteAllText($@"{UserFolderPath}\config.json", Configuration.AsJson());
                }
                else
                    Configuration = JsonSerializer.Deserialize<ConfigurationModel>(File.ReadAllText($@"{UserFolderPath}\config.json"), new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip });

                // Rewrite configuration to update it with any new fields not in existing configuration. Useful during update process and making sure the config always has updated annotations.
                // Users shouldn't need to re-configure every time they update the software.
                File.WriteAllText($@"{UserFolderPath}\config.json", Configuration.AsJson());
            }
            catch (Exception ex)
            {
                Log(LogEventType.Error, "An exception occurred while attempting to read or write the configuration file.");
                Log(ex);
                return;
            }

            IgnorableAudioPaths.Add(string.Empty);
            IgnorableAudioPaths.Add(XSGlobals.GetBuiltInAudioSourceString(XSAudioDefault.Default));
            IgnorableAudioPaths.Add(XSGlobals.GetBuiltInAudioSourceString(XSAudioDefault.Warning));
            IgnorableAudioPaths.Add(XSGlobals.GetBuiltInAudioSourceString(XSAudioDefault.Error));

            IgnorableIconPaths.Add(string.Empty);
            IgnorableIconPaths.Add(XSGlobals.GetBuiltInIconTypeString(XSIconDefaults.Default));
            IgnorableIconPaths.Add(XSGlobals.GetBuiltInIconTypeString(XSIconDefaults.Warning));
            IgnorableIconPaths.Add(XSGlobals.GetBuiltInIconTypeString(XSIconDefaults.Error));

            Subscriptions = new Dictionary<string, TailSubscription>();
            LogDetectionTimer = new Timer(new TimerCallback(LogDetectionTick), null, 0, Configuration.DirectoryPollFrequencyMilliseconds);

            Log(LogEventType.Info, $"Log detection timer initialized with poll frequency {Configuration.DirectoryPollFrequencyMilliseconds} and parse frequency {Configuration.ParseFrequencyMilliseconds}.");

            XSGlobals.DefaultSourceApp = "XSOverlay ChilloutVR Parser";
            XSGlobals.DefaultOpacity = Configuration.Opacity;
            XSGlobals.DefaultVolume = Configuration.NotificationVolume;

            try
            {
                Notifier = new XSNotifier();
            }
            catch (Exception ex)
            {
                Log(LogEventType.Error, "An exception occurred while constructing XSNotifier.");
                Log(ex);
                Exit();
            }

            Log(LogEventType.Info, $"XSNotifier initialized.");

            try
            {
                Notifier.SendNotification(new XSNotification()
                {
                    AudioPath = XSGlobals.GetBuiltInAudioSourceString(XSAudioDefault.Default),
                    Title = "Application Started",
                    Content = $"ChilloutVR Log Parser has initialized.",
                    Height = 110.0f
                });
            }
            catch (Exception ex)
            {
                Log(LogEventType.Error, "An exception occurred while sending initialization notification.");
                Log(ex);
                Exit();
            }

            await Task.Delay(-1); // Shutdown should be managed by XSO, so just... hang around. Maybe implement periodic checks to see if XSO is running to avoid being orphaned.
        }

        static void Exit()
        {
            Log(LogEventType.Info, "Disposing notifier and exiting application.");

            Notifier.Dispose();

            foreach (var item in Subscriptions)
                item.Value.Dispose();

            Subscriptions.Clear();

            Environment.Exit(-1);
        }

        static void Log(LogEventType type, string message)
        {
            DateTime now = DateTime.Now;
            string dateTimeStamp = $"[{now.Year:0000}/{now.Month:00}/{now.Day:00} {now.Hour:00}:{now.Minute:00}:{now.Second:00}]";

            lock (logMutex)
            {
                switch (type)
                {
                    case LogEventType.Error:
                        File.AppendAllText($@"{UserFolderPath}\Logs\{LogFileName}", $"{dateTimeStamp} [ERROR] {message}\r\n");
                        break;
                    case LogEventType.Event:
                        if (Configuration.LogNotificationEvents)
                            File.AppendAllText($@"{UserFolderPath}\Logs\{LogFileName}", $"{dateTimeStamp} [EVENT] {message}\r\n");
                        break;
                    case LogEventType.Info:
                        File.AppendAllText($@"{UserFolderPath}\Logs\{LogFileName}", $"{dateTimeStamp} [INFO] {message}\r\n");
                        break;
                }
            }
        }

        static void Log(Exception ex)
        {
            Log(LogEventType.Error, $"{ex.Message}\r\n{ex.InnerException}\r\n{ex.StackTrace}");
        }

        static void SendNotification(XSNotification notification)
        {
            try
            {
                Notifier.SendNotification(notification);
            }
            catch (Exception ex)
            {
                Log(LogEventType.Error, "An exception occurred while sending a routine event notification.");
                Log(ex);
                Exit();
            }
        }

        static void LogDetectionTick(object timerState)
        {
            string[] allFiles = Directory.GetFiles(Environment.ExpandEnvironmentVariables(Configuration.OutputLogRoot));
            foreach (string fn in allFiles)
                if (!Subscriptions.ContainsKey(fn) && fn == "Player.log")
                {
                    Subscriptions.Add(fn, new TailSubscription(fn, ParseTick, 0, Configuration.ParseFrequencyMilliseconds));
                    Log(LogEventType.Info, $"A tail subscription was added to {fn}");
                }
        }

        /// <summary>
        /// This is messy, but they've changed format on me often enough that it's difficult to care!
        /// </summary>
        /// <param name="content"></param>
        static void ParseTick(string content)
        {
            List<Tuple<EventType, XSNotification>> ToSend = new List<Tuple<EventType, XSNotification>>();

            if (!string.IsNullOrWhiteSpace(content))
            {
                string[] lines = content.Split('\n');

                foreach (string dirtyLine in lines)
                {
                    string line = Regex.Replace(dirtyLine
                        .Replace("\r", "")
                        .Replace("\n", "")
                        .Replace("\t", "")
                        .Trim(),
                        @"\s+", " ", RegexOptions.Multiline);

                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        int tocLoc = 0;
                        string[] tokens = line.Split(' ');

                        if (line.StartsWith("~   This Game has been MODIFIED using "))
                        {
                            Variables.MLDetected = true;
                        }
                        else if (line.Contains("[Core:ApiAuthenticationHelper] Successfully authenticated as: "))
                        {
                            Variables.User.Name = Regexes.GetUsername(line);
                            // if (Variables.MLDetected) System.Diagnostics.Process.Start($"mailto:team@abinteractive.net?subject=I'm%20using%20MelonLoader&body=Hello%2C%20my%20ingame%20username%20is%20{Variables.User.Name}%20and%20i%20have%20started%20ChilloutVR%20while%20MelonLoader%20is%20installed%20at%20{DateTime.Now}");
                        }
                        // Get new LastKnownLocationID here
                        else if (line.Contains("ApiGatherInstanceJoinInfo"))
                        {
                            Variables.World.UUID = Regexes.GetUUID(line);
                        }
                        // At this point, we have the location name/id and are transitioning.
                        else if (line.StartsWith("Connected to "))
                        {
                            Variables.Server = Regexes.GetServerEndpoint(line);
                            SilencedUntil = DateTime.Now.AddSeconds(Configuration.WorldJoinSilenceSeconds);

                            ToSend.Add(new Tuple<EventType, XSNotification>(EventType.WorldChange, new XSNotification()
                            {
                                Timeout = Configuration.WorldChangedNotificationTimeoutSeconds,
                                Icon = IgnorableIconPaths.Contains(Configuration.WorldChangedIconPath) ? Configuration.WorldChangedIconPath : Configuration.GetLocalResourcePath(Configuration.WorldChangedIconPath),
                                AudioPath = IgnorableAudioPaths.Contains(Configuration.WorldChangedAudioPath) ? Configuration.WorldChangedAudioPath : Configuration.GetLocalResourcePath(Configuration.WorldChangedAudioPath),
                                Title = new StringBuilder($"UUID: {Variables.World.UUID}").AppendLine($"Server: {Variables.Server}").ToString(),
                                Content = $"{(Configuration.DisplayJoinLeaveSilencedOverride ? "" : $"Silencing notifications for {Configuration.WorldJoinSilenceSeconds} seconds.")}",
                                Height = 110
                            }));

                            Log(LogEventType.Event, $"[CVR]World changed to {Variables.World.UUID}");
                        }
                        // Portal dropped
                        else if (line.Contains("[Core:Scheduler]") && line.Contains("Added scheduler task: ClearCanPlacePortal"))
                        {
                            ToSend.Add(new Tuple<EventType, XSNotification>(EventType.PortalDropped, new XSNotification()
                            {
                                Timeout = Configuration.PortalDroppedTimeoutSeconds,
                                Icon = IgnorableIconPaths.Contains(Configuration.PortalDroppedIconPath) ? Configuration.PortalDroppedIconPath : Configuration.GetLocalResourcePath(Configuration.PortalDroppedIconPath),
                                AudioPath = IgnorableAudioPaths.Contains(Configuration.PortalDroppedAudioPath) ? Configuration.PortalDroppedAudioPath : Configuration.GetLocalResourcePath(Configuration.PortalDroppedAudioPath),
                                Title = "You spawned a portal."
                            }));

                            Log(LogEventType.Event, $"[CVR]Portal dropped.");
                        }
                    }
                }
            }

            if (ToSend.Count > 0)
                foreach (Tuple<EventType, XSNotification> notification in ToSend)
                {
                    if (
                        (!CurrentlySilenced() && Configuration.DisplayPlayerJoined && notification.Item1 == EventType.PlayerJoin)
                        || (!CurrentlySilenced() && Configuration.DisplayPlayerLeft && notification.Item1 == EventType.PlayerLeft)
                        || (Configuration.DisplayWorldChanged && notification.Item1 == EventType.WorldChange)
                        || (Configuration.DisplayPortalDropped && notification.Item1 == EventType.PortalDropped)
                    )
                        SendNotification(notification.Item2);
                    else if (Configuration.DisplayMaximumKeywordsExceeded && notification.Item1 == EventType.KeywordsExceeded
                        && DateTime.Now > LastMaximumKeywordsNotification.AddSeconds(Configuration.MaximumKeywordsExceededCooldownSeconds))
                    {
                        LastMaximumKeywordsNotification = DateTime.Now;
                        SendNotification(notification.Item2);
                    }
                }
        }

        static bool CurrentlySilenced()
        {
            if (Configuration.DisplayJoinLeaveSilencedOverride)
                return false;

            if (DateTime.Now > SilencedUntil)
                return false;

            return true;
        }

    }
}
