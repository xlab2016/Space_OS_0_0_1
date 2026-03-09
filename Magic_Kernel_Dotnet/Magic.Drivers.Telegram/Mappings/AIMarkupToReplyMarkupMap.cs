using AI;
using Data.Mapping;
using Telegram.Bot.Types.ReplyMarkups;

namespace Magic.Drivers.Telegram.Mappings
{
    public class AIMarkupToReplyMarkupMap : MapBase2<AIMarkup, ReplyMarkup, MapOptions>
    {
        private readonly AIMarkupAButtonToKeyboardButtonMap _buttonMap = new();

        public override void MapCore(AIMarkup source, AIMarkup destination, MapOptions? options = null)
        {
            throw new NotImplementedException();
        }

        public override ReplyMarkup? MapCore(AIMarkup source, MapOptions? options = null)
        {
            var keyboard = source.Keyboard;

            if (keyboard != null)
            {
                if (keyboard.Remove)
                    return new ReplyKeyboardRemove();

                var result = new ReplyKeyboardMarkup
                {
                    ResizeKeyboard = true,
                    OneTimeKeyboard = true
                };

                if (keyboard.Buttons != null)
                    result.AddNewRow(_buttonMap.Map(keyboard.Buttons).ToArray());
                else if (keyboard.Rows != null)
                {
                    foreach (var item in keyboard.Rows)
                        result.AddNewRow(_buttonMap.Map(item.Buttons).ToArray());
                }

                return result;
            }

            return null;
        }

        public override AIMarkup ReverseMapCore(ReplyMarkup source, MapOptions? options = null)
        {
            throw new NotImplementedException();
        }
    }
}
