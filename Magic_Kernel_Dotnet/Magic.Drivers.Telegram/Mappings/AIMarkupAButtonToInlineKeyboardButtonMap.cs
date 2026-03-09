using AI;
using Data.Mapping;
using Telegram.Bot.Types.ReplyMarkups;

namespace Magic.Drivers.Telegram.Mappings
{
    public class AIMarkupAButtonToInlineKeyboardButtonMap : MapBase2<AIMarkup.AButton, InlineKeyboardButton, MapOptions>
    {
        public override void MapCore(AIMarkup.AButton source, AIMarkup.AButton destination, MapOptions? options = null)
        {
            throw new NotImplementedException();
        }

        public override InlineKeyboardButton MapCore(AIMarkup.AButton source, MapOptions? options = null)
        {
            if (!string.IsNullOrEmpty(source.Url))
                return InlineKeyboardButton.WithUrl(source.Label, source.Url);

            return InlineKeyboardButton.WithCallbackData(source.Label, source.Data ?? source.Label);
        }

        public override AIMarkup.AButton ReverseMapCore(InlineKeyboardButton source, MapOptions? options = null)
        {
            throw new NotImplementedException();
        }
    }
}
