using AI;
using Data.Mapping;
using Telegram.Bot.Types.ReplyMarkups;

namespace Magic.Drivers.Telegram.Mappings
{
    public class AIMarkupAButtonToKeyboardButtonMap : MapBase2<AIMarkup.AButton, KeyboardButton, MapOptions>
    {
        public override void MapCore(AIMarkup.AButton source, AIMarkup.AButton destination, MapOptions? options = null)
        {
            throw new NotImplementedException();
        }

        public override KeyboardButton MapCore(AIMarkup.AButton source, MapOptions? options = null)
        {
            switch (source.Handler)
            {
                case AIMarkup.AButton.RequestContactHandler:
                    return KeyboardButton.WithRequestContact(source.Label);
            }

            return new KeyboardButton { Text = source.Label };
        }

        public override AIMarkup.AButton ReverseMapCore(KeyboardButton source, MapOptions? options = null)
        {
            throw new NotImplementedException();
        }
    }
}
