using System.Timers;

namespace Hamstix.Haby.Client.Components.Toast
{
    public class ToastService : IDisposable
    {
        public event Action<string, ToastLevel> OnShow;
        public event Action OnHide;
        System.Timers.Timer Countdown;

        public void ShowToast(string message, ToastLevel level = ToastLevel.Info)
        {
            OnShow?.Invoke(message, level);
            StartCountdown();
        }

        void StartCountdown()
        {
            SetCountdown();
            if (Countdown.Enabled)
            {
                Countdown.Stop();
                Countdown.Start();
            }
            else
            {
                Countdown.Start();
            }
        }

        void SetCountdown()
        {
            if (Countdown == null)
            {
                Countdown = new System.Timers.Timer(10000);
                Countdown.Elapsed += HideToast;
                Countdown.AutoReset = false;
            }
        }

        void HideToast(object source, ElapsedEventArgs args)
        {
            OnHide?.Invoke();
        }

        public void Dispose()
        {
            Countdown?.Dispose();
        }
    }
}
