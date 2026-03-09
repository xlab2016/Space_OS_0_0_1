using Magic.Kernel.Devices.Streams.Drivers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Magic.Kernel.Devices.Streams
{
    public class Telegram : Messenger
    {
        public override async Task<DeviceOperationResult> OpenAsync()
        {
            _driver = new TelegramDriver(BotToken, DefaultChatId);
            return await base.OpenAsync();
        }
    }
}
