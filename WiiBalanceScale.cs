/*********************************************************************************
WiiBalanceScale

MIT License

Copyright (c) 2017 Bernhard Schelling

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
**********************************************************************************/

using System.Windows.Forms;
using WiimoteLib;

[assembly: System.Reflection.AssemblyTitle("WiiBalanceScale")]
[assembly: System.Reflection.AssemblyProduct("WiiBalanceScale")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]
[assembly: System.Runtime.InteropServices.ComVisible(false)]

namespace WiiBalanceScale
{
    internal class WiiBalanceScale
    {
        static WiiBalanceScaleForm f = null;
        static Wiimote bb = null;
        static ConnectionManager cm = null;
        static Timer BoardTimer = null;
        static float ZeroedWeight = 0;
        static float[] History = new float[100];
        static int HistoryBest = 1, HistoryCursor = -1;
        static string StarFull = "", StarEmpty = "";
        static bool UsePounds = false;

        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            f = new WiiBalanceScaleForm();
            StarFull = f.lblQuality.Text.Substring(0, 1);
            StarEmpty = f.lblQuality.Text.Substring(4, 1);
            f.lblWeight.Text = "";
            f.lblQuality.Text = "";
            f.lblUnit.Text = "";
            f.btnReset.Click += new System.EventHandler(btnReset_Click);
            f.lblUnit.Click += new System.EventHandler(lblUnit_Click);

            ConnectBalanceBoard(false);
            if (f == null) return; //connecting required application restart, end this process here

            BoardTimer = new System.Windows.Forms.Timer();
            BoardTimer.Interval = 50;
            BoardTimer.Tick += new System.EventHandler(BoardTimer_Tick);
            BoardTimer.Start();

            Application.Run(f);
            Shutdown();
        }

        static void Shutdown()
        {
            if (BoardTimer != null) { BoardTimer.Stop(); BoardTimer = null; }
            if (cm != null) { cm.Cancel(); cm = null; }
            if (f != null) { if (f.Visible) f.Close(); f = null; }
        }

        static void ConnectBalanceBoard(bool WasJustConnected)
        {
            bool Connected = true; try { bb = new Wiimote(); bb.Connect(); bb.SetLEDs(1); bb.GetStatus(); } catch { Connected = false; }

            if (!Connected || bb.WiimoteState.ExtensionType != ExtensionType.BalanceBoard)
            {
                if (ConnectionManager.ElevateProcessNeedRestart()) { Shutdown(); return; }
                if (cm == null) cm = new ConnectionManager();
                cm.ConnectNextWiiMote();
                return;
            }
            if (cm != null) { cm.Cancel(); cm = null; }

            f.lblWeight.Text = "...";
            f.lblQuality.Text = "";
            f.lblUnit.Text = "";
            f.Refresh();

            ZeroedWeight = 0.0f;
            int InitWeightCount = 0;
            for (int CountMax = (WasJustConnected ? 100 : 50); InitWeightCount < CountMax || bb.WiimoteState.BalanceBoardState.WeightKg == 0.0f; InitWeightCount++)
            {
                if (bb.WiimoteState.BalanceBoardState.WeightKg < -200) break;
                ZeroedWeight += bb.WiimoteState.BalanceBoardState.WeightKg;
                bb.GetStatus();
            }
            ZeroedWeight /= (float)InitWeightCount;

            //start with half full quality bar
            HistoryCursor = HistoryBest = History.Length / 2;
            for (int i = 0; i < History.Length; i++)
                History[i] = (i > HistoryCursor ? float.MinValue : ZeroedWeight);
        }

        static void BoardTimer_Tick(object sender, System.EventArgs e)
        {
            if (cm != null)
            {
                if (cm.IsRunning())
                {
                    f.lblWeight.Text = "WAIT...";
                    f.lblQuality.Text = (f.lblQuality.Text.Length >= 5 ? "" : f.lblQuality.Text) + "6";
                    return;
                }
                if (cm.HadError())
                {
                    BoardTimer.Stop();
                    System.Windows.Forms.MessageBox.Show(f, "No compatible bluetooth adapter found - Quitting", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Shutdown();
                    return;
                }
                ConnectBalanceBoard(true);
                return;
            }

            float kg = bb.WiimoteState.BalanceBoardState.WeightKg, HistorySum = 0.0f, MaxHist = kg, MinHist = kg, MaxDiff = 0.0f;
            if (kg < -200)
            {
                ConnectBalanceBoard(false);
                return;
            }

            HistoryCursor++;
            History[HistoryCursor % History.Length] = kg;
            for (HistoryBest = 0; HistoryBest < History.Length; HistoryBest++)
            {
                float HistoryEntry = History[(HistoryCursor + History.Length - HistoryBest) % History.Length];
                if (System.Math.Abs(MaxHist - HistoryEntry) > 1.0f) break;
                if (System.Math.Abs(MinHist - HistoryEntry) > 1.0f) break;
                if (HistoryEntry > MaxHist) MaxHist = HistoryEntry;
                if (HistoryEntry > MinHist) MinHist = HistoryEntry;
                float Diff = System.Math.Max(System.Math.Abs(HistoryEntry - kg), System.Math.Abs((HistorySum + HistoryEntry) / (HistoryBest + 1) - kg));
                if (Diff > MaxDiff) MaxDiff = Diff;
                if (Diff > 1.0f) break;
                HistorySum += HistoryEntry;
            }

            kg = HistorySum / HistoryBest - ZeroedWeight;

            float accuracy = 1.0f / HistoryBest;
            kg = (float)System.Math.Floor(kg / accuracy + 0.5f) * accuracy;

            if (UsePounds) kg *= 2.20462262f;
            f.lblWeight.Text = (kg >= 0.0f && kg < 10.0f ? " " : "") + kg.ToString("0.00") + (kg <= -10.0f ? "" : "0");

            f.lblQuality.Text = "";
            for (int i = 0; i < 5; i++)
                f.lblQuality.Text += (i < ((HistoryBest + 5) / (History.Length / 5)) ? StarFull : StarEmpty);
            f.lblUnit.Text = (UsePounds ? "lbs" : "kg");
        }

        static void btnReset_Click(object sender, System.EventArgs e)
        {
            float HistorySum = 0.0f;
            for (int i = 0; i < HistoryBest; i++)
                HistorySum += History[(HistoryCursor + History.Length - i) % History.Length];
            ZeroedWeight = HistorySum / HistoryBest;
        }

        static void lblUnit_Click(object sender, System.EventArgs e)
        {
            UsePounds = !UsePounds;
        }
    }
}
