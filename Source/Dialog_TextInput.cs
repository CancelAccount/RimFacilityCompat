using System;
using UnityEngine;
using Verse;

namespace FacilityCompat
{
    /// <summary>
    /// 简易文本输入对话框，用于导入导出的文件名/路径输入
    /// </summary>
    public class Dialog_TextInput : Window
    {
        private string text;
        private readonly string title;
        private readonly Action<string> onConfirm;
        private readonly int maxLength;

        public Dialog_TextInput(string initialText, string title, Action<string> onConfirm, int maxLength = 200)
        {
            this.text = initialText ?? "";
            this.title = title;
            this.onConfirm = onConfirm;
            this.maxLength = maxLength;
            this.doCloseX = true;
            this.closeOnAccept = false;
            this.absorbInputAroundWindow = true;
        }

        public override Vector2 InitialSize => new Vector2(500f, 150f);

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 30f), title);
            var inputRect = new Rect(0f, 35f, inRect.width - 120f, 30f);
            text = Widgets.TextField(inputRect, text, maxLength);

            var btnRect = new Rect(inRect.width - 110f, 35f, 110f, 30f);
            if (Widgets.ButtonText(btnRect, "OK".Translate()) && text.Trim().Length > 0)
            {
                onConfirm(text.Trim());
                Close();
            }

            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return
                && text.Trim().Length > 0)
            {
                onConfirm(text.Trim());
                Close();
                Event.current.Use();
            }
        }
    }
}
