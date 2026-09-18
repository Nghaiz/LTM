using UnityEngine;

namespace Ironfront.Unity.Ui
{
    /// <summary>
    /// Typed access to the small, curated subset of the Game UI Collection used by Ironfront.
    /// Keeping resource names here prevents scene and runtime styling code from drifting apart.
    /// </summary>
    public sealed class GameUiCollectionResources
    {
        private const string Root = "Ironfront/UI/GameCollection/";
        private static GameUiCollectionResources _instance;

        private GameUiCollectionResources()
        {
            PanelCyan = LoadSprite("PanelCyan");
            ButtonCyan = LoadSprite("ButtonCyan");
            ButtonYellow = LoadSprite("ButtonYellow");
            ButtonBorderCyan = LoadSprite("ButtonBorderCyan");
            ButtonBorderYellow = LoadSprite("ButtonBorderYellow");
            IconSpeaker = LoadSprite("IconSpeaker");
            IconMuted = LoadSprite("IconMuted");
            IconBack = LoadSprite("IconBack");
            IconClose = LoadSprite("IconClose");
            IconConfirm = LoadSprite("IconConfirm");
        }

        public Sprite PanelCyan { get; }
        public Sprite ButtonCyan { get; }
        public Sprite ButtonYellow { get; }
        public Sprite ButtonBorderCyan { get; }
        public Sprite ButtonBorderYellow { get; }
        public Sprite IconSpeaker { get; }
        public Sprite IconMuted { get; }
        public Sprite IconBack { get; }
        public Sprite IconClose { get; }
        public Sprite IconConfirm { get; }

        public static GameUiCollectionResources Load()
        {
            return _instance ?? (_instance = new GameUiCollectionResources());
        }

        private static Sprite LoadSprite(string name)
        {
            return Resources.Load<Sprite>(Root + name);
        }
    }
}
