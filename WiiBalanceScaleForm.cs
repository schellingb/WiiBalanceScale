/*********************************************************************************
WiiBalanceScale

MIT License

Copyright (c) 2017-2023 Bernhard Schelling

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

using System;
using System.Drawing;
using System.Windows.Forms;

namespace WiiBalanceScale
{
    class WiiBalanceScaleForm : Form
    {
        public WiiBalanceScaleForm()
        {
            InitializeComponent();
            try { this.Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName); } catch (Exception) { }
        }

        internal Label lblWeight;
        internal Button btnReset;
        internal Label lblQuality;
        internal Label lblUnit;
        internal GroupBox unitSelector;
        internal RadioButton unitSelectorKg;
        internal RadioButton unitSelectorLb;
        internal RadioButton unitSelectorStone;

        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.lblWeight = new System.Windows.Forms.Label();
            this.btnReset = new System.Windows.Forms.Button();
            this.lblQuality = new System.Windows.Forms.Label();
            this.lblUnit = new System.Windows.Forms.Label();
            this.unitSelector = new System.Windows.Forms.GroupBox();
            this.unitSelectorStone = new System.Windows.Forms.RadioButton();
            this.unitSelectorKg = new System.Windows.Forms.RadioButton();
            this.unitSelectorLb = new System.Windows.Forms.RadioButton();
            this.unitSelector.SuspendLayout();
            this.SuspendLayout();
            // 
            // lblWeight
            // 
            this.lblWeight.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.lblWeight.Font = new System.Drawing.Font("Lucida Console", 100F);
            this.lblWeight.ImageAlign = System.Drawing.ContentAlignment.TopCenter;
            this.lblWeight.Location = new System.Drawing.Point(0, 66);
            this.lblWeight.Name = "lblWeight";
            this.lblWeight.Size = new System.Drawing.Size(884, 187);
            this.lblWeight.TabIndex = 0;
            this.lblWeight.Text = "088.710";
            this.lblWeight.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            // 
            // btnReset
            // 
            this.btnReset.Font = new System.Drawing.Font("Microsoft Sans Serif", 30F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(128)));
            this.btnReset.Location = new System.Drawing.Point(88, 301);
            this.btnReset.Name = "btnReset";
            this.btnReset.Size = new System.Drawing.Size(710, 64);
            this.btnReset.TabIndex = 7;
            this.btnReset.Text = "Zero";
            this.btnReset.UseVisualStyleBackColor = true;
            // 
            // lblQuality
            // 
            this.lblQuality.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.lblQuality.Font = new System.Drawing.Font("Wingdings", 60F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel);
            this.lblQuality.Location = new System.Drawing.Point(0, 237);
            this.lblQuality.Name = "lblQuality";
            this.lblQuality.Size = new System.Drawing.Size(884, 61);
            this.lblQuality.TabIndex = 8;
            this.lblQuality.Text = "®®®¡¡";
            this.lblQuality.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            // 
            // lblUnit
            // 
            this.lblUnit.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.lblUnit.Font = new System.Drawing.Font("Microsoft Sans Serif", 33F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(128)));
            this.lblUnit.Location = new System.Drawing.Point(735, 158);
            this.lblUnit.Margin = new System.Windows.Forms.Padding(2, 0, 2, 0);
            this.lblUnit.Name = "lblUnit";
            this.lblUnit.Size = new System.Drawing.Size(140, 60);
            this.lblUnit.TabIndex = 9;
            this.lblUnit.Text = "kg";
            // 
            // unitSelector
            // 
            this.unitSelector.Controls.Add(this.unitSelectorStone);
            this.unitSelector.Controls.Add(this.unitSelectorKg);
            this.unitSelector.Controls.Add(this.unitSelectorLb);
            this.unitSelector.Location = new System.Drawing.Point(10, 9);
            this.unitSelector.Name = "unitSelector";
            this.unitSelector.Size = new System.Drawing.Size(865, 45);
            this.unitSelector.TabIndex = 0;
            this.unitSelector.TabStop = false;
            this.unitSelector.Text = "Units";
            this.unitSelector.Visible = false;
            // 
            // unitSelectorStone
            // 
            this.unitSelectorStone.AutoSize = true;
            this.unitSelectorStone.Location = new System.Drawing.Point(295, 19);
            this.unitSelectorStone.Name = "unitSelectorStone";
            this.unitSelectorStone.Size = new System.Drawing.Size(129, 17);
            this.unitSelectorStone.TabIndex = 2;
            this.unitSelectorStone.TabStop = true;
            this.unitSelectorStone.Text = "Stone/Pounds (st/lbs)";
            this.unitSelectorStone.UseVisualStyleBackColor = true;
            // 
            // unitSelectorKg
            // 
            this.unitSelectorKg.AutoSize = true;
            this.unitSelectorKg.Location = new System.Drawing.Point(13, 19);
            this.unitSelectorKg.Name = "unitSelectorKg";
            this.unitSelectorKg.Size = new System.Drawing.Size(91, 17);
            this.unitSelectorKg.TabIndex = 0;
            this.unitSelectorKg.TabStop = true;
            this.unitSelectorKg.Text = "Kilograms (kg)";
            this.unitSelectorKg.UseVisualStyleBackColor = true;
            // 
            // unitSelectorLb
            // 
            this.unitSelectorLb.AutoSize = true;
            this.unitSelectorLb.Location = new System.Drawing.Point(154, 19);
            this.unitSelectorLb.Name = "unitSelectorLb";
            this.unitSelectorLb.Size = new System.Drawing.Size(83, 17);
            this.unitSelectorLb.TabIndex = 1;
            this.unitSelectorLb.TabStop = true;
            this.unitSelectorLb.Text = "Pounds (lbs)";
            this.unitSelectorLb.UseVisualStyleBackColor = true;
            // 
            // WiiBalanceScaleForm
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(884, 381);
            this.Controls.Add(this.unitSelector);
            this.Controls.Add(this.lblUnit);
            this.Controls.Add(this.btnReset);
            this.Controls.Add(this.lblQuality);
            this.Controls.Add(this.lblWeight);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.Name = "WiiBalanceScaleForm";
            this.Text = "Wii Balance Scale";
            this.unitSelector.ResumeLayout(false);
            this.unitSelector.PerformLayout();
            this.ResumeLayout(false);

        }
        #endregion
    }
}