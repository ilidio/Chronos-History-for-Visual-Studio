using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace ChronosHistoryVS
{
    [Guid("D1C8C8C8-C8C8-C8C8-C8C8-D1C8C8C8C8C8")]
    public class ChronosOptionsPage : DialogPage
    {
        private string apiKey = "";
        private string language = "English";
        private string model = "gemini-2.0-flash";

        [Category("AI Settings")]
        [DisplayName("Gemini API Key")]
        [Description("The API key for Google Gemini AI. Get one at https://aistudio.google.com/")]
        public string ApiKey
        {
            get => apiKey;
            set => apiKey = value;
        }

        [Category("AI Settings")]
        [DisplayName("Language")]
        [Description("Preferred language for AI generated content.")]
        public string Language
        {
            get => language;
            set => language = value;
        }

        [Category("AI Settings")]
        [DisplayName("Model")]
        [Description("Gemini model to use.")]
        public string Model
        {
            get => model;
            set => model = value;
        }
    }
}
