using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AD.Common.DataStructures;
using AD.Common.Helpers;
using ADClientSDK;
using System.Threading;
using Telegram.Bot;
using System.ComponentModel;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Telegram.Bot.Types;
using Telegram.Bot.Types.InlineKeyboardButtons;
using System.Security;
using System.Net;

namespace RemoteBot
{
    class Program
    {
        static IAdClient _client;
        static TelegramBotClient _bot;
        static BackgroundWorker _bgw;
        static ChatId _chatId;
        static DateTime _lastReportDate = DateTime.MinValue;
        static DateTime _lastBalanceUpdate = DateTime.MinValue;
        static DateTime _lastConnectionCheck = DateTime.MinValue;
        const int RUB_ID = 174368;
        static ObjectEntity[] _allObjects;

        static string _login;
        static SecureString _pass;

        static void Main(string[] args)
        {
            Console.Write("Enter login:");
            _login = Console.ReadLine();
            Console.Write("Enter password:");
            ConsoleKey key = default(ConsoleKey);

            _pass = new SecureString();
            while (key!= ConsoleKey.Enter)
            {
                var input = Console.ReadKey(true);
                key = input.Key;
                if (key == ConsoleKey.Backspace)
                    if (_pass.Length > 0)
                        _pass.RemoveAt(_pass.Length - 1);
                if (char.IsLetter(input.KeyChar) || char.IsDigit(input.KeyChar))
                    _pass.AppendChar(input.KeyChar);
            }

            Console.WriteLine("Ok");

            Packer.Initialize();
            _client = new AdClient();
            if (_client == null)
                return;

            Console.WriteLine("Waiting..");
            Thread.Sleep(3000);

            _allObjects = _client.Dictionaries.GetObjects();
            _bot = new TelegramBotClient(Properties.RemoteBot.Default.Token);
            _bgw = new BackgroundWorker();
            _bgw.DoWork += CheckBot;
            _bgw.RunWorkerAsync();
            Console.WriteLine("RemoteBot {0} started.", Properties.RemoteBot.Default.Name);
            _client.OnConnectionChanged += OnConnectionChanged;

            Console.ReadKey();
        }

        private static async void CheckBot(object sender, DoWorkEventArgs e)
        {
            int offset = 0;
            var userName = Properties.RemoteBot.Default.TelegramUserName;
            while (true)
            {
                try
                {
                    var updates = await _bot.GetUpdatesAsync(offset);
                    foreach (var upd in updates.Where(o =>
                    (o.Type == UpdateType.MessageUpdate && o.Message.From.Username == userName)
                    || (o.Type == UpdateType.CallbackQueryUpdate && o.CallbackQuery.From.Username == userName)))
                    {
                        var data = upd.Message != null ? upd.Message.Text : upd.CallbackQuery.Data;
                        switch (data)
                        {
                            case "/start":
                                _chatId = new ChatId(upd.Message.From.Id);
                                SendText("Hello, {0}. Robot monitor started.",
                                    upd.Message.From.FirstName);
                                break;
                            case "/balances":
                                double totalBal;
                                SendText(GetPositionsText(out totalBal));
                                break;
                            case "/reconnect":
                                Reconnect();
                                break;
                            default:
                                SendText("Unknown command.");
                                break;
                        }
                        offset = upd.Id + 1;
                    }
                    var now = DateTime.Now;
                    if ((now.DayOfWeek != DayOfWeek.Sunday) && (now.DayOfWeek != DayOfWeek.Saturday))
                    {
                        if (now.Hour == Properties.RemoteBot.Default.ReportHour)
                            if (now.Minute == Properties.RemoteBot.Default.ReportMinute)
                                if (now - _lastReportDate > TimeSpan.FromMinutes(1))
                                {
                                    _lastReportDate = now;
                                    double totalBal;
                                    SendText(GetPositionsText(out totalBal));
                                }
                        if (now.Hour == Properties.RemoteBot.Default.BalanceUpdateHour)
                            if (now.Minute == Properties.RemoteBot.Default.BalanceUpdateMinute)
                                if (now - _lastBalanceUpdate > TimeSpan.FromMinutes(1))
                                {
                                    double totalBal;
                                    GetPositionsText(out totalBal);
                                    var url = string.Format(@Properties.RemoteBot.Default.BalanceReportString,
                                        Properties.RemoteBot.Default.BalanceReportKey,
                                        now.ToShortDateString(),
                                        totalBal);
                                    using (var client = new WebClient())
                                    {
                                        var resp = client.DownloadString(url);
                                        Console.WriteLine("[{1}] Updating balances: {0}", url, resp);
                                        SendText("Every day balance updated: {0}", resp);
                                    }
                                    _lastBalanceUpdate = now;
                                }
                    }

                    if (DateTime.Now - _lastConnectionCheck > TimeSpan.FromMinutes(Properties.RemoteBot.Default.ReconnectPeriod))
                    {
                        _lastConnectionCheck = DateTime.Now;
                        foreach (var fend in Enum.GetValues(typeof(FrontEndType)))
                        {
                            var fet = (FrontEndType)fend;
                            if ((fet == FrontEndType.AllTypes) || (fet == FrontEndType.RealTimeBirzInfoDelayedServer))
                                continue;
                            var status = _client.GetConnectionStatus(fet);
                            if (status != ConnectionStatus.Authorized)
                            {
                                SendText("{0} server is {1}. Trying to reconnect.. ", fend.ToString(), status.ToString());
                                Reconnect();
                            }
                        }
                    }

                }
                catch (Exception ex)
                {
                    //Console.WriteLine(ex);
                }
                Thread.Sleep(1000);
            }
        }

        private static string GetPositionsText(out double totalBalance)
        {
            totalBalance = 0;
            double totalProfit = 0;
            var positions = _client.Portfolio.GetPositions();
            var sb = new StringBuilder();
            sb.AppendLine("Balances:");

            foreach (var pos in positions)
            {
                var ins = _allObjects.First(o => o.IdObject == pos.IdObject);
                var isMoney = ins.IdObject == RUB_ID;
                if (isMoney)
                    totalBalance += pos.SubAccNalPos;
                else
                    totalProfit += pos.VariationMargin;
                sb.AppendLine(string.Format("{0}: {1} {2} RUB", 
                    pos.IdAccount, 
                    ins.NameObject, 
                    isMoney ? pos.SubAccNalPos.ToString() : 
                    (pos.VariationMargin > 0 ? 
                    string.Format("+{0}", pos.VariationMargin) 
                    : string.Format("-{0}", pos.VariationMargin)
                    )));
            }
            sb.Append(string.Format("Total balance: {0} {1}{2} RUB",
                totalBalance, 
                totalProfit > 0 ? "+" : "-",
                totalProfit));
            return sb.ToString();
        }

        private static void SendText(string format, params string[] args)
        {
            SendText(string.Format(format, args));
        }

        private static async void SendText(string text)
        {
            try
            {
                if (_chatId == null)
                    return;
                var keyboard = new InlineKeyboardMarkup(
                                                    new InlineKeyboardButton[][]
                                                    {
                                                        new InlineKeyboardButton[] 
                                                        {
                                                            new InlineKeyboardCallbackButton("Get balances","/balances"),
                                                            new InlineKeyboardCallbackButton("Reconnect","/reconnect")
                                                        }
                                                    }
                                                );
                var result = await _bot.SendTextMessageAsync(_chatId, text, replyMarkup: keyboard);
                Console.WriteLine("[{0}, {1}]: {2}", result.Date, result.Chat.Username, result.Text);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        private static void Reconnect()
        {
            _client.Dissconnect();
            Thread.Sleep(5000);
            var pass = new System.Net.NetworkCredential(string.Empty, _pass).Password;
            _client.Connect(_login, pass);
            pass = null;
        }

        private static void OnConnectionChanged(FrontEndType arg1, ConnectionStatus arg2)
        {
            SendText(string.Format("Connection status changed [{0}]: {1}", arg1, arg2));
        }
    }
}
