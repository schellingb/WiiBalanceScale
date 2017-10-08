//////////////////////////////////////////////////////////////////////////////////
//	Wiimote.cs
//	WiimoteCollection.cs
//	Events.cs
//	DataTypes.cs
//	HIDImports.cs
//	Managed Wiimote Library
//	Written by Brian Peek (http://www.brianpeek.com/)
//	for MSDN's Coding4Fun (http://msdn.microsoft.com/coding4fun/)
//	Visit http://blogs.msdn.com/coding4fun/archive/2007/03/14/1879033.aspx
//	   and http://www.codeplex.com/WiimoteLib
//	for more information
//	Merged into a single file WiimoteLib.cs
//	   Based on https://github.com/BrianPeek/WiimoteLib
//	   With changes from https://github.com/simphax/WiimoteLib
//     Added ConnectionManager class to auto connect to devices
//////////////////////////////////////////////////////////////////////////////////

/*********************************************************************************
MIT License

Copyright (c) 2017 Brian Peek

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
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using Microsoft.Win32.SafeHandles;
using System.Threading;
using System.Collections.ObjectModel;

// if we're building the MSRS version, we need to bring in the MSRS Attributes
// if we're not doing the MSRS build then define some fake attribute classes for DataMember/DataContract
#if MSRS
	using Microsoft.Dss.Core.Attributes;
#else
	sealed class DataContract : Attribute
	{
	}

	sealed class DataMember: Attribute
	{
	}
#endif

namespace WiimoteLib
{
	/// <summary>
	/// Implementation of Wiimote
	/// </summary>
	public class Wiimote : IDisposable
	{
		/// <summary>
		/// Event raised when Wiimote state is changed
		/// </summary>
		public event EventHandler<WiimoteChangedEventArgs> WiimoteChanged;

		/// <summary>
		/// Event raised when an extension is inserted or removed
		/// </summary>
		public event EventHandler<WiimoteExtensionChangedEventArgs> WiimoteExtensionChanged;

		// VID = Nintendo, PID = Wiimote
		private const int VID = 0x057e;
		private const int PID = 0x0306;
		private const int PID_TR = 0x0330;

		// sure, we could find this out the hard way using HID, but trust me, it's 22
		private const int REPORT_LENGTH = 22;

		// Wiimote output commands
		private enum OutputReport : byte
		{
			LEDs			= 0x11,
			Type			= 0x12,
			IR				= 0x13,
			Status			= 0x15,
			WriteMemory		= 0x16,
			ReadMemory		= 0x17,
			IR2				= 0x1a,
		};

		// Wiimote registers
		private const int REGISTER_IR				= 0x04b00030;
		private const int REGISTER_IR_SENSITIVITY_1	= 0x04b00000;
		private const int REGISTER_IR_SENSITIVITY_2	= 0x04b0001a;
		private const int REGISTER_IR_MODE			= 0x04b00033;

		private const int REGISTER_EXTENSION_INIT_1			= 0x04a400f0;
		private const int REGISTER_EXTENSION_INIT_2			= 0x04a400fb;
		private const int REGISTER_EXTENSION_TYPE			= 0x04a400fa;
		private const int REGISTER_EXTENSION_CALIBRATION	= 0x04a40020;

		// length between board sensors
		private const int BSL = 43;

		// width between board sensors
		private const int BSW = 24;

		// read/write handle to the device
		private SafeFileHandle mHandle;

		// a pretty .NET stream to read/write from/to
		private FileStream mStream;

		// report buffer
		private readonly byte[] mBuff = new byte[REPORT_LENGTH];

		// read data buffer
		private byte[] mReadBuff;

		// address to read from
		private int mAddress;

		// size of requested read
		private short mSize;

		// current state of controller
		private readonly WiimoteState mWiimoteState = new WiimoteState();

		// event for read data processing
		private readonly AutoResetEvent mReadDone = new AutoResetEvent(false);
		private readonly AutoResetEvent mWriteDone = new AutoResetEvent(false);

		// event for status report
		private readonly AutoResetEvent mStatusDone = new AutoResetEvent(false);

		// use a different method to write reports
		private bool mAltWriteMethod;

		// HID device path of this Wiimote
		private string mDevicePath = string.Empty;

		// unique ID
		private readonly Guid mID = Guid.NewGuid();

		// delegate used for enumerating found Wiimotes
		internal delegate bool WiimoteFoundDelegate(string devicePath);

		// kilograms to pounds
		private const float KG2LB = 2.20462262f;

		/// <summary>
		/// Default constructor
		/// </summary>
		public Wiimote()
		{
		}

		internal Wiimote(string devicePath)
		{
			mDevicePath = devicePath;
		}

		/// <summary>
		/// Connect to the first-found Wiimote
		/// </summary>
		/// <exception cref="WiimoteNotFoundException">Wiimote not found in HID device list</exception>
		public void Connect()
		{
			if(string.IsNullOrEmpty(mDevicePath))
				FindWiimote(WiimoteFound);
			else
				OpenWiimoteDeviceHandle(mDevicePath);
		}

		internal static void FindWiimote(WiimoteFoundDelegate wiimoteFound)
		{
			int index = 0;
			bool found = false;
			Guid guid;
			SafeFileHandle mHandle;

			// get the GUID of the HID class
			HIDImports.HidD_GetHidGuid(out guid);

			// get a handle to all devices that are part of the HID class
			// Fun fact:  DIGCF_PRESENT worked on my machine just fine.  I reinstalled Vista, and now it no longer finds the Wiimote with that parameter enabled...
			IntPtr hDevInfo = HIDImports.SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, HIDImports.DIGCF_DEVICEINTERFACE);// | HIDImports.DIGCF_PRESENT);

			// create a new interface data struct and initialize its size
			HIDImports.SP_DEVICE_INTERFACE_DATA diData = new HIDImports.SP_DEVICE_INTERFACE_DATA();
			diData.cbSize = Marshal.SizeOf(diData);

			// get a device interface to a single device (enumerate all devices)
			while(HIDImports.SetupDiEnumDeviceInterfaces(hDevInfo, IntPtr.Zero, ref guid, index, ref diData))
			{
				UInt32 size;

				// get the buffer size for this device detail instance (returned in the size parameter)
				HIDImports.SetupDiGetDeviceInterfaceDetail(hDevInfo, ref diData, IntPtr.Zero, 0, out size, IntPtr.Zero);

				// create a detail struct and set its size
				HIDImports.SP_DEVICE_INTERFACE_DETAIL_DATA diDetail = new HIDImports.SP_DEVICE_INTERFACE_DETAIL_DATA();

				// yeah, yeah...well, see, on Win x86, cbSize must be 5 for some reason.  On x64, apparently 8 is what it wants.
				// someday I should figure this out.  Thanks to Paul Miller on this...
				diDetail.cbSize = (uint)(IntPtr.Size == 8 ? 8 : 5);

				// actually get the detail struct
				if(HIDImports.SetupDiGetDeviceInterfaceDetail(hDevInfo, ref diData, ref diDetail, size, out size, IntPtr.Zero))
				{
					Debug.WriteLine(string.Format("{0}: {1} - {2}", index, diDetail.DevicePath, Marshal.GetLastWin32Error()));

					// open a read/write handle to our device using the DevicePath returned
					mHandle = HIDImports.CreateFile(diDetail.DevicePath, FileAccess.ReadWrite, FileShare.ReadWrite, IntPtr.Zero, FileMode.Open, HIDImports.EFileAttributes.Overlapped, IntPtr.Zero);

					// create an attributes struct and initialize the size
					HIDImports.HIDD_ATTRIBUTES attrib = new HIDImports.HIDD_ATTRIBUTES();
					attrib.Size = Marshal.SizeOf(attrib);

					// get the attributes of the current device
					if(HIDImports.HidD_GetAttributes(mHandle.DangerousGetHandle(), ref attrib))
					{
						// if the vendor and product IDs match up
						if(attrib.VendorID == VID && (attrib.ProductID == PID || attrib.ProductID == PID_TR))
						{
							// it's a Wiimote
							Debug.WriteLine("Found one!");
							found = true;

							// fire the callback function...if the callee doesn't care about more Wiimotes, break out
							if(!wiimoteFound(diDetail.DevicePath))
								break;
						}
					}
					mHandle.Close();
				}
				else
				{
					// failed to get the detail struct
					throw new WiimoteException("SetupDiGetDeviceInterfaceDetail failed on index " + index);
				}

				// move to the next device
				index++;
			}

			// clean up our list
			HIDImports.SetupDiDestroyDeviceInfoList(hDevInfo);

			// if we didn't find a Wiimote, throw an exception
			if(!found)
				throw new WiimoteNotFoundException("No Wiimotes found in HID device list.");
		}

		private bool WiimoteFound(string devicePath)
		{
			mDevicePath = devicePath;

			// if we didn't find a Wiimote, throw an exception
			OpenWiimoteDeviceHandle(mDevicePath);

			return false;
		}

		private void OpenWiimoteDeviceHandle(string devicePath)
		{
			// open a read/write handle to our device using the DevicePath returned
			mHandle = HIDImports.CreateFile(devicePath, FileAccess.ReadWrite, FileShare.ReadWrite, IntPtr.Zero, FileMode.Open, HIDImports.EFileAttributes.Overlapped, IntPtr.Zero);

			// create an attributes struct and initialize the size
			HIDImports.HIDD_ATTRIBUTES attrib = new HIDImports.HIDD_ATTRIBUTES();
			attrib.Size = Marshal.SizeOf(attrib);

			// get the attributes of the current device
			if(HIDImports.HidD_GetAttributes(mHandle.DangerousGetHandle(), ref attrib))
			{
				// if the vendor and product IDs match up
				if (attrib.VendorID == VID && (attrib.ProductID == PID || attrib.ProductID == PID_TR))
				{
					// create a nice .NET FileStream wrapping the handle above
					mStream = new FileStream(mHandle, FileAccess.ReadWrite, REPORT_LENGTH, true);

					// start an async read operation on it
					BeginAsyncRead();

					// read the calibration info from the controller
					try
					{
						ReadWiimoteCalibration();
					}
					catch
					{
						// if we fail above, try the alternate HID writes
						mAltWriteMethod = true;
						ReadWiimoteCalibration();
					}

					// force a status check to get the state of any extensions plugged in at startup
					GetStatus();
				}
				else
				{
					// otherwise this isn't the controller, so close up the file handle
					mHandle.Close();				
					throw new WiimoteException("Attempted to open a non-Wiimote device.");
				}
			}
		}

		/// <summary>
		/// Disconnect from the controller and stop reading data from it
		/// </summary>
		public void Disconnect()
		{
			// close up the stream and handle
			if(mStream != null)
				mStream.Close();

			if(mHandle != null)
				mHandle.Close();
		}

		/// <summary>
		/// Start reading asynchronously from the controller
		/// </summary>
		private void BeginAsyncRead()
		{
			// if the stream is valid and ready
			if(mStream != null && mStream.CanRead)
			{
				// setup the read and the callback
				byte[] buff = new byte[REPORT_LENGTH];
				mStream.BeginRead(buff, 0, REPORT_LENGTH, new AsyncCallback(OnReadData), buff);
			}
		}

		/// <summary>
		/// Callback when data is ready to be processed
		/// </summary>
		/// <param name="ar">State information for the callback</param>
		private void OnReadData(IAsyncResult ar)
		{
			// grab the byte buffer
			byte[] buff = (byte[])ar.AsyncState;

			try
			{
				// end the current read
				mStream.EndRead(ar);

				// parse it
				if(ParseInputReport(buff))
				{
					// post an event
					if(WiimoteChanged != null)
						WiimoteChanged(this, new WiimoteChangedEventArgs(mWiimoteState));
				}

				// start reading again
				BeginAsyncRead();
			}
			catch(OperationCanceledException)
			{
				Debug.WriteLine("OperationCanceledException");
			}
			catch (IOException)
			{
				Debug.WriteLine("IOException");
			}
			catch (ObjectDisposedException)
			{
				Debug.WriteLine("ObjectDisposedException");
			}
			catch (Exception)
			{
				Debug.WriteLine("Unkown exception");
			}

		}

		/// <summary>
		/// Parse a report sent by the Wiimote
		/// </summary>
		/// <param name="buff">Data buffer to parse</param>
		/// <returns>Returns a boolean noting whether an event needs to be posted</returns>
		private bool ParseInputReport(byte[] buff)
		{
			InputReport type = (InputReport)buff[0];

			switch(type)
			{
				case InputReport.Buttons:
					ParseButtons(buff);
					break;
				case InputReport.ButtonsAccel:
					ParseButtons(buff);
					ParseAccel(buff);
					break;
				case InputReport.IRAccel:
					ParseButtons(buff);
					ParseAccel(buff);
					ParseIR(buff);
					break;
				case InputReport.ButtonsExtension:
					ParseButtons(buff);
					ParseExtension(buff, 3);
					break;
				case InputReport.ExtensionAccel:
					ParseButtons(buff);
					ParseAccel(buff);
					ParseExtension(buff, 6);
					break;
				case InputReport.IRExtensionAccel:
					ParseButtons(buff);
					ParseAccel(buff);
					ParseIR(buff);
					ParseExtension(buff, 16);
					break;
				case InputReport.Status:
					ParseButtons(buff);
					mWiimoteState.BatteryRaw = buff[6];
					mWiimoteState.Battery = (((100.0f * 48.0f * (float)((int)buff[6] / 48.0f))) / 192.0f);

					// get the real LED values in case the values from SetLEDs() somehow becomes out of sync, which really shouldn't be possible
					mWiimoteState.LEDState.LED1 = (buff[3] & 0x10) != 0;
					mWiimoteState.LEDState.LED2 = (buff[3] & 0x20) != 0;
					mWiimoteState.LEDState.LED3 = (buff[3] & 0x40) != 0;
					mWiimoteState.LEDState.LED4 = (buff[3] & 0x80) != 0;

					// extension connected?
					bool extension = (buff[3] & 0x02) != 0;
					Debug.WriteLine("Extension: " + extension);

					if(mWiimoteState.Extension != extension)
					{
						mWiimoteState.Extension = extension;

						if(extension)
						{
							BeginAsyncRead();
							InitializeExtension();
						}
						else
							mWiimoteState.ExtensionType = ExtensionType.None;

						// only fire the extension changed event if we have a real extension (i.e. not a balance board)
						if(WiimoteExtensionChanged != null && mWiimoteState.ExtensionType != ExtensionType.BalanceBoard)
							WiimoteExtensionChanged(this, new WiimoteExtensionChangedEventArgs(mWiimoteState.ExtensionType, mWiimoteState.Extension));
					}
					mStatusDone.Set();
					break;
				case InputReport.ReadData:
					ParseButtons(buff);
					ParseReadData(buff);
					break;
				case InputReport.OutputReportAck:
					Debug.WriteLine("ack: " + buff[0] + " " +  buff[1] + " " +buff[2] + " " +buff[3] + " " +buff[4]);
					mWriteDone.Set();
					break;
				default:
					Debug.WriteLine("Unknown report type: " + type.ToString("x"));
					return false;
			}

			return true;
		}

		/// <summary>
		/// Handles setting up an extension when plugged in
		/// </summary>
		private void InitializeExtension()
		{
			WriteData(REGISTER_EXTENSION_INIT_1, 0x55);
			WriteData(REGISTER_EXTENSION_INIT_2, 0x00);

			// start reading again
			BeginAsyncRead();

			byte[] buff = ReadData(REGISTER_EXTENSION_TYPE, 6);
			long type = ((long)buff[0] << 40) | ((long)buff[1] << 32) | ((long)buff[2]) << 24 | ((long)buff[3]) << 16 | ((long)buff[4]) << 8 | buff[5];

			switch((ExtensionType)type)
			{
				case ExtensionType.None:
				case ExtensionType.ParitallyInserted:
					mWiimoteState.Extension = false;
					mWiimoteState.ExtensionType = ExtensionType.None;
					return;
				case ExtensionType.NewNunchuk:
					mWiimoteState.ExtensionType = ExtensionType.Nunchuk;
					this.SetReportType(InputReport.ButtonsExtension, true);
					break;
				case ExtensionType.ClassicControllerPro:
					mWiimoteState.ExtensionType = ExtensionType.ClassicController;
					this.SetReportType(InputReport.ButtonsExtension, true);
					break;
				case ExtensionType.Nunchuk:
				case ExtensionType.ClassicController:
				case ExtensionType.Guitar:
				case ExtensionType.BalanceBoard:
				case ExtensionType.Drums:
					mWiimoteState.ExtensionType = (ExtensionType)type;
					this.SetReportType(InputReport.ButtonsExtension, true);
					break;
				default:
					throw new WiimoteException("Unknown extension controller found: " + type.ToString("x"));
			}

			switch(mWiimoteState.ExtensionType)
			{
				case ExtensionType.Nunchuk:
					buff = ReadData(REGISTER_EXTENSION_CALIBRATION, 16);

					mWiimoteState.NunchukState.CalibrationInfo.AccelCalibration.X0 = buff[0];
					mWiimoteState.NunchukState.CalibrationInfo.AccelCalibration.Y0 = buff[1];
					mWiimoteState.NunchukState.CalibrationInfo.AccelCalibration.Z0 = buff[2];
					mWiimoteState.NunchukState.CalibrationInfo.AccelCalibration.XG = buff[4];
					mWiimoteState.NunchukState.CalibrationInfo.AccelCalibration.YG = buff[5];
					mWiimoteState.NunchukState.CalibrationInfo.AccelCalibration.ZG = buff[6];
					mWiimoteState.NunchukState.CalibrationInfo.MaxX = buff[8];
					mWiimoteState.NunchukState.CalibrationInfo.MinX = buff[9];
					mWiimoteState.NunchukState.CalibrationInfo.MidX = buff[10];
					mWiimoteState.NunchukState.CalibrationInfo.MaxY = buff[11];
					mWiimoteState.NunchukState.CalibrationInfo.MinY = buff[12];
					mWiimoteState.NunchukState.CalibrationInfo.MidY = buff[13];
					break;
				case ExtensionType.ClassicController:
					buff = ReadData(REGISTER_EXTENSION_CALIBRATION, 16);

					mWiimoteState.ClassicControllerState.CalibrationInfo.MaxXL = (byte)(buff[0] >> 2) > 0 ? (byte)(buff[0] >> 2) : (byte)64;
					mWiimoteState.ClassicControllerState.CalibrationInfo.MinXL = (byte)(buff[1] >> 2) > 0 ? (byte)(buff[1] >> 2) : (byte)0;
					mWiimoteState.ClassicControllerState.CalibrationInfo.MidXL = (byte)(buff[2] >> 2) > 0 ? (byte)(buff[2] >> 2) : (byte)32;
					mWiimoteState.ClassicControllerState.CalibrationInfo.MaxYL = (byte)(buff[3] >> 2) > 0 ? (byte)(buff[3] >> 2) : (byte)64;
					mWiimoteState.ClassicControllerState.CalibrationInfo.MinYL = (byte)(buff[4] >> 2) > 0 ? (byte)(buff[4] >> 2) : (byte)0;
					mWiimoteState.ClassicControllerState.CalibrationInfo.MidYL = (byte)(buff[5] >> 2) > 0 ? (byte)(buff[5] >> 2) : (byte)32;

					mWiimoteState.ClassicControllerState.CalibrationInfo.MaxXR = (byte)(buff[6] >> 3) > 0 ? (byte)(buff[6] >> 3) : (byte)32;
					mWiimoteState.ClassicControllerState.CalibrationInfo.MinXR = (byte)(buff[7] >> 3) > 0 ? (byte)(buff[7] >> 3) : (byte)0;
					mWiimoteState.ClassicControllerState.CalibrationInfo.MidXR = (byte)(buff[8] >> 3) > 0 ? (byte)(buff[8] >> 3) : (byte)16;
					mWiimoteState.ClassicControllerState.CalibrationInfo.MaxYR = (byte)(buff[9] >> 3) > 0 ? (byte)(buff[9] >> 3) : (byte)32;
					mWiimoteState.ClassicControllerState.CalibrationInfo.MinYR = (byte)(buff[10] >> 3) > 0 ? (byte)(buff[10] >> 3) : (byte)0;
					mWiimoteState.ClassicControllerState.CalibrationInfo.MidYR = (byte)(buff[11] >> 3) > 0 ? (byte)(buff[11] >> 3) : (byte)16;

					// this doesn't seem right...
//					mWiimoteState.ClassicControllerState.AccelCalibrationInfo.MinTriggerL = (byte)(buff[12] >> 3);
//					mWiimoteState.ClassicControllerState.AccelCalibrationInfo.MaxTriggerL = (byte)(buff[14] >> 3);
//					mWiimoteState.ClassicControllerState.AccelCalibrationInfo.MinTriggerR = (byte)(buff[13] >> 3);
//					mWiimoteState.ClassicControllerState.AccelCalibrationInfo.MaxTriggerR = (byte)(buff[15] >> 3);
					mWiimoteState.ClassicControllerState.CalibrationInfo.MinTriggerL = 0;
					mWiimoteState.ClassicControllerState.CalibrationInfo.MaxTriggerL = 31;
					mWiimoteState.ClassicControllerState.CalibrationInfo.MinTriggerR = 0;
					mWiimoteState.ClassicControllerState.CalibrationInfo.MaxTriggerR = 31;
					break;
				case ExtensionType.Guitar:
				case ExtensionType.Drums:
					// there appears to be no calibration data returned by the guitar controller
					break;
				case ExtensionType.BalanceBoard:
					buff = ReadData(REGISTER_EXTENSION_CALIBRATION, 32);

					mWiimoteState.BalanceBoardState.CalibrationInfo.Kg0.TopRight =		(short)((short)buff[4] << 8 | buff[5]);
					mWiimoteState.BalanceBoardState.CalibrationInfo.Kg0.BottomRight =	(short)((short)buff[6] << 8 | buff[7]);
					mWiimoteState.BalanceBoardState.CalibrationInfo.Kg0.TopLeft =		(short)((short)buff[8] << 8 | buff[9]);
					mWiimoteState.BalanceBoardState.CalibrationInfo.Kg0.BottomLeft =	(short)((short)buff[10] << 8 | buff[11]);

					mWiimoteState.BalanceBoardState.CalibrationInfo.Kg17.TopRight =		(short)((short)buff[12] << 8 | buff[13]);
					mWiimoteState.BalanceBoardState.CalibrationInfo.Kg17.BottomRight =	(short)((short)buff[14] << 8 | buff[15]);
					mWiimoteState.BalanceBoardState.CalibrationInfo.Kg17.TopLeft =		(short)((short)buff[16] << 8 | buff[17]);
					mWiimoteState.BalanceBoardState.CalibrationInfo.Kg17.BottomLeft =	(short)((short)buff[18] << 8 | buff[19]);

					mWiimoteState.BalanceBoardState.CalibrationInfo.Kg34.TopRight =		(short)((short)buff[20] << 8 | buff[21]);
					mWiimoteState.BalanceBoardState.CalibrationInfo.Kg34.BottomRight =	(short)((short)buff[22] << 8 | buff[23]);
					mWiimoteState.BalanceBoardState.CalibrationInfo.Kg34.TopLeft =		(short)((short)buff[24] << 8 | buff[25]);
					mWiimoteState.BalanceBoardState.CalibrationInfo.Kg34.BottomLeft =	(short)((short)buff[26] << 8 | buff[27]);
					break;
			}
		}

		/// <summary>
		/// Decrypts data sent from the extension to the Wiimote
		/// </summary>
		/// <param name="buff">Data buffer</param>
		/// <returns>Byte array containing decoded data</returns>
		private byte[] DecryptBuffer(byte[] buff)
		{
			for(int i = 0; i < buff.Length; i++)
				buff[i] = (byte)(((buff[i] ^ 0x17) + 0x17) & 0xff);

			return buff;
		}

		/// <summary>
		/// Parses a standard button report into the ButtonState struct
		/// </summary>
		/// <param name="buff">Data buffer</param>
		private void ParseButtons(byte[] buff)
		{
			mWiimoteState.ButtonState.A		= (buff[2] & 0x08) != 0;
			mWiimoteState.ButtonState.B		= (buff[2] & 0x04) != 0;
			mWiimoteState.ButtonState.Minus	= (buff[2] & 0x10) != 0;
			mWiimoteState.ButtonState.Home	= (buff[2] & 0x80) != 0;
			mWiimoteState.ButtonState.Plus	= (buff[1] & 0x10) != 0;
			mWiimoteState.ButtonState.One	= (buff[2] & 0x02) != 0;
			mWiimoteState.ButtonState.Two	= (buff[2] & 0x01) != 0;
			mWiimoteState.ButtonState.Up	= (buff[1] & 0x08) != 0;
			mWiimoteState.ButtonState.Down	= (buff[1] & 0x04) != 0;
			mWiimoteState.ButtonState.Left	= (buff[1] & 0x01) != 0;
			mWiimoteState.ButtonState.Right	= (buff[1] & 0x02) != 0;
		}

		/// <summary>
		/// Parse accelerometer data
		/// </summary>
		/// <param name="buff">Data buffer</param>
		private void ParseAccel(byte[] buff)
		{
			mWiimoteState.AccelState.RawValues.X = buff[3];
			mWiimoteState.AccelState.RawValues.Y = buff[4];
			mWiimoteState.AccelState.RawValues.Z = buff[5];

			mWiimoteState.AccelState.Values.X = (float)((float)mWiimoteState.AccelState.RawValues.X - ((int)mWiimoteState.AccelCalibrationInfo.X0)) / 
											((float)mWiimoteState.AccelCalibrationInfo.XG - ((int)mWiimoteState.AccelCalibrationInfo.X0));
			mWiimoteState.AccelState.Values.Y = (float)((float)mWiimoteState.AccelState.RawValues.Y - mWiimoteState.AccelCalibrationInfo.Y0) /
											((float)mWiimoteState.AccelCalibrationInfo.YG - mWiimoteState.AccelCalibrationInfo.Y0);
			mWiimoteState.AccelState.Values.Z = (float)((float)mWiimoteState.AccelState.RawValues.Z - mWiimoteState.AccelCalibrationInfo.Z0) /
											((float)mWiimoteState.AccelCalibrationInfo.ZG - mWiimoteState.AccelCalibrationInfo.Z0);
		}

		/// <summary>
		/// Parse IR data from report
		/// </summary>
		/// <param name="buff">Data buffer</param>
		private void ParseIR(byte[] buff)
		{
			mWiimoteState.IRState.IRSensors[0].RawPosition.X = buff[6] | ((buff[8] >> 4) & 0x03) << 8;
			mWiimoteState.IRState.IRSensors[0].RawPosition.Y = buff[7] | ((buff[8] >> 6) & 0x03) << 8;

			switch(mWiimoteState.IRState.Mode)
			{
				case IRMode.Basic:
					mWiimoteState.IRState.IRSensors[1].RawPosition.X = buff[9]  | ((buff[8] >> 0) & 0x03) << 8;
					mWiimoteState.IRState.IRSensors[1].RawPosition.Y = buff[10] | ((buff[8] >> 2) & 0x03) << 8;

					mWiimoteState.IRState.IRSensors[2].RawPosition.X = buff[11] | ((buff[13] >> 4) & 0x03) << 8;
					mWiimoteState.IRState.IRSensors[2].RawPosition.Y = buff[12] | ((buff[13] >> 6) & 0x03) << 8;

					mWiimoteState.IRState.IRSensors[3].RawPosition.X = buff[14] | ((buff[13] >> 0) & 0x03) << 8;
					mWiimoteState.IRState.IRSensors[3].RawPosition.Y = buff[15] | ((buff[13] >> 2) & 0x03) << 8;

					mWiimoteState.IRState.IRSensors[0].Size = 0x00;
					mWiimoteState.IRState.IRSensors[1].Size = 0x00;
					mWiimoteState.IRState.IRSensors[2].Size = 0x00;
					mWiimoteState.IRState.IRSensors[3].Size = 0x00;

					mWiimoteState.IRState.IRSensors[0].Found = !(buff[6] == 0xff && buff[7] == 0xff);
					mWiimoteState.IRState.IRSensors[1].Found = !(buff[9] == 0xff && buff[10] == 0xff);
					mWiimoteState.IRState.IRSensors[2].Found = !(buff[11] == 0xff && buff[12] == 0xff);
					mWiimoteState.IRState.IRSensors[3].Found = !(buff[14] == 0xff && buff[15] == 0xff);
					break;
				case IRMode.Extended:
					mWiimoteState.IRState.IRSensors[1].RawPosition.X = buff[9]  | ((buff[11] >> 4) & 0x03) << 8;
					mWiimoteState.IRState.IRSensors[1].RawPosition.Y = buff[10] | ((buff[11] >> 6) & 0x03) << 8;
					mWiimoteState.IRState.IRSensors[2].RawPosition.X = buff[12] | ((buff[14] >> 4) & 0x03) << 8;
					mWiimoteState.IRState.IRSensors[2].RawPosition.Y = buff[13] | ((buff[14] >> 6) & 0x03) << 8;
					mWiimoteState.IRState.IRSensors[3].RawPosition.X = buff[15] | ((buff[17] >> 4) & 0x03) << 8;
					mWiimoteState.IRState.IRSensors[3].RawPosition.Y = buff[16] | ((buff[17] >> 6) & 0x03) << 8;

					mWiimoteState.IRState.IRSensors[0].Size = buff[8] & 0x0f;
					mWiimoteState.IRState.IRSensors[1].Size = buff[11] & 0x0f;
					mWiimoteState.IRState.IRSensors[2].Size = buff[14] & 0x0f;
					mWiimoteState.IRState.IRSensors[3].Size = buff[17] & 0x0f;

					mWiimoteState.IRState.IRSensors[0].Found = !(buff[6] == 0xff && buff[7] == 0xff && buff[8] == 0xff);
					mWiimoteState.IRState.IRSensors[1].Found = !(buff[9] == 0xff && buff[10] == 0xff && buff[11] == 0xff);
					mWiimoteState.IRState.IRSensors[2].Found = !(buff[12] == 0xff && buff[13] == 0xff && buff[14] == 0xff);
					mWiimoteState.IRState.IRSensors[3].Found = !(buff[15] == 0xff && buff[16] == 0xff && buff[17] == 0xff);
					break;
			}

			mWiimoteState.IRState.IRSensors[0].Position.X = (float)(mWiimoteState.IRState.IRSensors[0].RawPosition.X / 1023.5f);
			mWiimoteState.IRState.IRSensors[1].Position.X = (float)(mWiimoteState.IRState.IRSensors[1].RawPosition.X / 1023.5f);
			mWiimoteState.IRState.IRSensors[2].Position.X = (float)(mWiimoteState.IRState.IRSensors[2].RawPosition.X / 1023.5f);
			mWiimoteState.IRState.IRSensors[3].Position.X = (float)(mWiimoteState.IRState.IRSensors[3].RawPosition.X / 1023.5f);

			mWiimoteState.IRState.IRSensors[0].Position.Y = (float)(mWiimoteState.IRState.IRSensors[0].RawPosition.Y / 767.5f);
			mWiimoteState.IRState.IRSensors[1].Position.Y = (float)(mWiimoteState.IRState.IRSensors[1].RawPosition.Y / 767.5f);
			mWiimoteState.IRState.IRSensors[2].Position.Y = (float)(mWiimoteState.IRState.IRSensors[2].RawPosition.Y / 767.5f);
			mWiimoteState.IRState.IRSensors[3].Position.Y = (float)(mWiimoteState.IRState.IRSensors[3].RawPosition.Y / 767.5f);

			if(mWiimoteState.IRState.IRSensors[0].Found && mWiimoteState.IRState.IRSensors[1].Found)
			{
				mWiimoteState.IRState.RawMidpoint.X = (mWiimoteState.IRState.IRSensors[1].RawPosition.X + mWiimoteState.IRState.IRSensors[0].RawPosition.X) / 2;
				mWiimoteState.IRState.RawMidpoint.Y = (mWiimoteState.IRState.IRSensors[1].RawPosition.Y + mWiimoteState.IRState.IRSensors[0].RawPosition.Y) / 2;
		
				mWiimoteState.IRState.Midpoint.X = (mWiimoteState.IRState.IRSensors[1].Position.X + mWiimoteState.IRState.IRSensors[0].Position.X) / 2.0f;
				mWiimoteState.IRState.Midpoint.Y = (mWiimoteState.IRState.IRSensors[1].Position.Y + mWiimoteState.IRState.IRSensors[0].Position.Y) / 2.0f;
			}
			else
				mWiimoteState.IRState.Midpoint.X = mWiimoteState.IRState.Midpoint.Y = 0.0f;
		}

		/// <summary>
		/// Parse data from an extension controller
		/// </summary>
		/// <param name="buff">Data buffer</param>
		/// <param name="offset">Offset into data buffer</param>
		private void ParseExtension(byte[] buff, int offset)
		{
			switch(mWiimoteState.ExtensionType)
			{
				case ExtensionType.Nunchuk:
					mWiimoteState.NunchukState.RawJoystick.X = buff[offset];
					mWiimoteState.NunchukState.RawJoystick.Y = buff[offset + 1];
					mWiimoteState.NunchukState.AccelState.RawValues.X = buff[offset + 2];
					mWiimoteState.NunchukState.AccelState.RawValues.Y = buff[offset + 3];
					mWiimoteState.NunchukState.AccelState.RawValues.Z = buff[offset + 4];

					mWiimoteState.NunchukState.C = (buff[offset + 5] & 0x02) == 0;
					mWiimoteState.NunchukState.Z = (buff[offset + 5] & 0x01) == 0;

					mWiimoteState.NunchukState.AccelState.Values.X = (float)((float)mWiimoteState.NunchukState.AccelState.RawValues.X - mWiimoteState.NunchukState.CalibrationInfo.AccelCalibration.X0) / 
													((float)mWiimoteState.NunchukState.CalibrationInfo.AccelCalibration.XG - mWiimoteState.NunchukState.CalibrationInfo.AccelCalibration.X0);
					mWiimoteState.NunchukState.AccelState.Values.Y = (float)((float)mWiimoteState.NunchukState.AccelState.RawValues.Y - mWiimoteState.NunchukState.CalibrationInfo.AccelCalibration.Y0) /
													((float)mWiimoteState.NunchukState.CalibrationInfo.AccelCalibration.YG - mWiimoteState.NunchukState.CalibrationInfo.AccelCalibration.Y0);
					mWiimoteState.NunchukState.AccelState.Values.Z = (float)((float)mWiimoteState.NunchukState.AccelState.RawValues.Z - mWiimoteState.NunchukState.CalibrationInfo.AccelCalibration.Z0) /
													((float)mWiimoteState.NunchukState.CalibrationInfo.AccelCalibration.ZG - mWiimoteState.NunchukState.CalibrationInfo.AccelCalibration.Z0);

					if(mWiimoteState.NunchukState.CalibrationInfo.MaxX != 0x00)
						mWiimoteState.NunchukState.Joystick.X = (float)((float)mWiimoteState.NunchukState.RawJoystick.X - mWiimoteState.NunchukState.CalibrationInfo.MidX) / 
												((float)mWiimoteState.NunchukState.CalibrationInfo.MaxX - mWiimoteState.NunchukState.CalibrationInfo.MinX);

					if(mWiimoteState.NunchukState.CalibrationInfo.MaxY != 0x00)
						mWiimoteState.NunchukState.Joystick.Y = (float)((float)mWiimoteState.NunchukState.RawJoystick.Y - mWiimoteState.NunchukState.CalibrationInfo.MidY) / 
												((float)mWiimoteState.NunchukState.CalibrationInfo.MaxY - mWiimoteState.NunchukState.CalibrationInfo.MinY);

					break;

				case ExtensionType.ClassicController:
					mWiimoteState.ClassicControllerState.RawJoystickL.X = (byte)(buff[offset] & 0x3f);
					mWiimoteState.ClassicControllerState.RawJoystickL.Y = (byte)(buff[offset + 1] & 0x3f);
					mWiimoteState.ClassicControllerState.RawJoystickR.X = (byte)((buff[offset + 2] >> 7) | (buff[offset + 1] & 0xc0) >> 5 | (buff[offset] & 0xc0) >> 3);
					mWiimoteState.ClassicControllerState.RawJoystickR.Y = (byte)(buff[offset + 2] & 0x1f);

					mWiimoteState.ClassicControllerState.RawTriggerL = (byte)(((buff[offset + 2] & 0x60) >> 2) | (buff[offset + 3] >> 5));
					mWiimoteState.ClassicControllerState.RawTriggerR = (byte)(buff[offset + 3] & 0x1f);

					mWiimoteState.ClassicControllerState.ButtonState.TriggerR	= (buff[offset + 4] & 0x02) == 0;
					mWiimoteState.ClassicControllerState.ButtonState.Plus		= (buff[offset + 4] & 0x04) == 0;
					mWiimoteState.ClassicControllerState.ButtonState.Home		= (buff[offset + 4] & 0x08) == 0;
					mWiimoteState.ClassicControllerState.ButtonState.Minus		= (buff[offset + 4] & 0x10) == 0;
					mWiimoteState.ClassicControllerState.ButtonState.TriggerL	= (buff[offset + 4] & 0x20) == 0;
					mWiimoteState.ClassicControllerState.ButtonState.Down		= (buff[offset + 4] & 0x40) == 0;
					mWiimoteState.ClassicControllerState.ButtonState.Right		= (buff[offset + 4] & 0x80) == 0;

					mWiimoteState.ClassicControllerState.ButtonState.Up			= (buff[offset + 5] & 0x01) == 0;
					mWiimoteState.ClassicControllerState.ButtonState.Left		= (buff[offset + 5] & 0x02) == 0;
					mWiimoteState.ClassicControllerState.ButtonState.ZR			= (buff[offset + 5] & 0x04) == 0;
					mWiimoteState.ClassicControllerState.ButtonState.X			= (buff[offset + 5] & 0x08) == 0;
					mWiimoteState.ClassicControllerState.ButtonState.A			= (buff[offset + 5] & 0x10) == 0;
					mWiimoteState.ClassicControllerState.ButtonState.Y			= (buff[offset + 5] & 0x20) == 0;
					mWiimoteState.ClassicControllerState.ButtonState.B			= (buff[offset + 5] & 0x40) == 0;
					mWiimoteState.ClassicControllerState.ButtonState.ZL			= (buff[offset + 5] & 0x80) == 0;

					if(mWiimoteState.ClassicControllerState.CalibrationInfo.MaxXL != 0x00)
						mWiimoteState.ClassicControllerState.JoystickL.X = (float)((float)mWiimoteState.ClassicControllerState.RawJoystickL.X - mWiimoteState.ClassicControllerState.CalibrationInfo.MidXL) / 
						(float)(mWiimoteState.ClassicControllerState.CalibrationInfo.MaxXL - mWiimoteState.ClassicControllerState.CalibrationInfo.MinXL);

					if(mWiimoteState.ClassicControllerState.CalibrationInfo.MaxYL != 0x00)
						mWiimoteState.ClassicControllerState.JoystickL.Y = (float)((float)mWiimoteState.ClassicControllerState.RawJoystickL.Y - mWiimoteState.ClassicControllerState.CalibrationInfo.MidYL) / 
						(float)(mWiimoteState.ClassicControllerState.CalibrationInfo.MaxYL - mWiimoteState.ClassicControllerState.CalibrationInfo.MinYL);

					if(mWiimoteState.ClassicControllerState.CalibrationInfo.MaxXR != 0x00)
						mWiimoteState.ClassicControllerState.JoystickR.X = (float)((float)mWiimoteState.ClassicControllerState.RawJoystickR.X - mWiimoteState.ClassicControllerState.CalibrationInfo.MidXR) / 
						(float)(mWiimoteState.ClassicControllerState.CalibrationInfo.MaxXR - mWiimoteState.ClassicControllerState.CalibrationInfo.MinXR);

					if(mWiimoteState.ClassicControllerState.CalibrationInfo.MaxYR != 0x00)
						mWiimoteState.ClassicControllerState.JoystickR.Y = (float)((float)mWiimoteState.ClassicControllerState.RawJoystickR.Y - mWiimoteState.ClassicControllerState.CalibrationInfo.MidYR) / 
						(float)(mWiimoteState.ClassicControllerState.CalibrationInfo.MaxYR - mWiimoteState.ClassicControllerState.CalibrationInfo.MinYR);

					if(mWiimoteState.ClassicControllerState.CalibrationInfo.MaxTriggerL != 0x00)
						mWiimoteState.ClassicControllerState.TriggerL = (mWiimoteState.ClassicControllerState.RawTriggerL) / 
						(float)(mWiimoteState.ClassicControllerState.CalibrationInfo.MaxTriggerL - mWiimoteState.ClassicControllerState.CalibrationInfo.MinTriggerL);

					if(mWiimoteState.ClassicControllerState.CalibrationInfo.MaxTriggerR != 0x00)
						mWiimoteState.ClassicControllerState.TriggerR = (mWiimoteState.ClassicControllerState.RawTriggerR) / 
						(float)(mWiimoteState.ClassicControllerState.CalibrationInfo.MaxTriggerR - mWiimoteState.ClassicControllerState.CalibrationInfo.MinTriggerR);
					break;

				case ExtensionType.Guitar:
					mWiimoteState.GuitarState.GuitarType = ((buff[offset] & 0x80) == 0) ? GuitarType.GuitarHeroWorldTour : GuitarType.GuitarHero3;

					mWiimoteState.GuitarState.ButtonState.Plus		= (buff[offset + 4] & 0x04) == 0;
					mWiimoteState.GuitarState.ButtonState.Minus		= (buff[offset + 4] & 0x10) == 0;
					mWiimoteState.GuitarState.ButtonState.StrumDown	= (buff[offset + 4] & 0x40) == 0;

					mWiimoteState.GuitarState.ButtonState.StrumUp		= (buff[offset + 5] & 0x01) == 0;
					mWiimoteState.GuitarState.FretButtonState.Yellow	= (buff[offset + 5] & 0x08) == 0;
					mWiimoteState.GuitarState.FretButtonState.Green		= (buff[offset + 5] & 0x10) == 0;
					mWiimoteState.GuitarState.FretButtonState.Blue		= (buff[offset + 5] & 0x20) == 0;
					mWiimoteState.GuitarState.FretButtonState.Red		= (buff[offset + 5] & 0x40) == 0;
					mWiimoteState.GuitarState.FretButtonState.Orange	= (buff[offset + 5] & 0x80) == 0;

					// it appears the joystick values are only 6 bits
					mWiimoteState.GuitarState.RawJoystick.X	= (buff[offset + 0] & 0x3f);
					mWiimoteState.GuitarState.RawJoystick.Y	= (buff[offset + 1] & 0x3f);

					// and the whammy bar is only 5 bits
					mWiimoteState.GuitarState.RawWhammyBar			= (byte)(buff[offset + 3] & 0x1f);

					mWiimoteState.GuitarState.Joystick.X			= (float)(mWiimoteState.GuitarState.RawJoystick.X - 0x1f) / 0x3f;	// not fully accurate, but close
					mWiimoteState.GuitarState.Joystick.Y			= (float)(mWiimoteState.GuitarState.RawJoystick.Y - 0x1f) / 0x3f;	// not fully accurate, but close
					mWiimoteState.GuitarState.WhammyBar				= (float)(mWiimoteState.GuitarState.RawWhammyBar) / 0x0a;	// seems like there are 10 positions?

					mWiimoteState.GuitarState.TouchbarState.Yellow	= false;
					mWiimoteState.GuitarState.TouchbarState.Green	= false;
					mWiimoteState.GuitarState.TouchbarState.Blue	= false;
					mWiimoteState.GuitarState.TouchbarState.Red		= false;
					mWiimoteState.GuitarState.TouchbarState.Orange	= false;

					switch(buff[offset + 2] & 0x1f)
					{
						case 0x04:
							mWiimoteState.GuitarState.TouchbarState.Green = true;
							break;
						case 0x07:
							mWiimoteState.GuitarState.TouchbarState.Green = true;
							mWiimoteState.GuitarState.TouchbarState.Red = true;
							break;
						case 0x0a:
							mWiimoteState.GuitarState.TouchbarState.Red = true;
							break;
						case 0x0c:
						case 0x0d:
							mWiimoteState.GuitarState.TouchbarState.Red = true;
							mWiimoteState.GuitarState.TouchbarState.Yellow = true;
							break;
						case 0x12:
						case 0x13:
							mWiimoteState.GuitarState.TouchbarState.Yellow = true;
							break;
						case 0x14:
						case 0x15:
							mWiimoteState.GuitarState.TouchbarState.Yellow = true;
							mWiimoteState.GuitarState.TouchbarState.Blue = true;
							break;
						case 0x17:
						case 0x18:
							mWiimoteState.GuitarState.TouchbarState.Blue = true;
							break;
						case 0x1a:
							mWiimoteState.GuitarState.TouchbarState.Blue = true;
							mWiimoteState.GuitarState.TouchbarState.Orange = true;
							break;
						case 0x1f:
							mWiimoteState.GuitarState.TouchbarState.Orange = true;
							break;
					}
					break;

				case ExtensionType.Drums:
					// it appears the joystick values are only 6 bits
					mWiimoteState.DrumsState.RawJoystick.X	= (buff[offset + 0] & 0x3f);
					mWiimoteState.DrumsState.RawJoystick.Y	= (buff[offset + 1] & 0x3f);

					mWiimoteState.DrumsState.Plus			= (buff[offset + 4] & 0x04) == 0;
					mWiimoteState.DrumsState.Minus			= (buff[offset + 4] & 0x10) == 0;

					mWiimoteState.DrumsState.Pedal			= (buff[offset + 5] & 0x04) == 0;
					mWiimoteState.DrumsState.Blue			= (buff[offset + 5] & 0x08) == 0;
					mWiimoteState.DrumsState.Green			= (buff[offset + 5] & 0x10) == 0;
					mWiimoteState.DrumsState.Yellow			= (buff[offset + 5] & 0x20) == 0;
					mWiimoteState.DrumsState.Red			= (buff[offset + 5] & 0x40) == 0;
					mWiimoteState.DrumsState.Orange			= (buff[offset + 5] & 0x80) == 0;

					mWiimoteState.DrumsState.Joystick.X		= (float)(mWiimoteState.DrumsState.RawJoystick.X - 0x1f) / 0x3f;	// not fully accurate, but close
					mWiimoteState.DrumsState.Joystick.Y		= (float)(mWiimoteState.DrumsState.RawJoystick.Y - 0x1f) / 0x3f;	// not fully accurate, but close

					if((buff[offset + 2] & 0x40) == 0)
					{
						int pad = (buff[offset + 2] >> 1) & 0x1f;
						int velocity = (buff[offset + 3] >> 5);

						if(velocity != 7)
						{
							switch(pad)
							{
								case 0x1b:
									mWiimoteState.DrumsState.PedalVelocity = velocity;
									break;
								case 0x19:
									mWiimoteState.DrumsState.RedVelocity = velocity;
									break;
								case 0x11:
									mWiimoteState.DrumsState.YellowVelocity = velocity;
									break;
								case 0x0f:
									mWiimoteState.DrumsState.BlueVelocity = velocity;
									break;
								case 0x0e:
									mWiimoteState.DrumsState.OrangeVelocity = velocity;
									break;
								case 0x12:
									mWiimoteState.DrumsState.GreenVelocity = velocity;
									break;
							}
						}
					}

					break;

				case ExtensionType.BalanceBoard:
					mWiimoteState.BalanceBoardState.SensorValuesRaw.TopRight = (short)((short)buff[offset + 0] << 8 | buff[offset + 1]);
					mWiimoteState.BalanceBoardState.SensorValuesRaw.BottomRight = (short)((short)buff[offset + 2] << 8 | buff[offset + 3]);
					mWiimoteState.BalanceBoardState.SensorValuesRaw.TopLeft = (short)((short)buff[offset + 4] << 8 | buff[offset + 5]);
					mWiimoteState.BalanceBoardState.SensorValuesRaw.BottomLeft = (short)((short)buff[offset + 6] << 8 | buff[offset + 7]);

					mWiimoteState.BalanceBoardState.SensorValuesKg.TopLeft = GetBalanceBoardSensorValue(mWiimoteState.BalanceBoardState.SensorValuesRaw.TopLeft, mWiimoteState.BalanceBoardState.CalibrationInfo.Kg0.TopLeft, mWiimoteState.BalanceBoardState.CalibrationInfo.Kg17.TopLeft, mWiimoteState.BalanceBoardState.CalibrationInfo.Kg34.TopLeft);
					mWiimoteState.BalanceBoardState.SensorValuesKg.TopRight = GetBalanceBoardSensorValue(mWiimoteState.BalanceBoardState.SensorValuesRaw.TopRight, mWiimoteState.BalanceBoardState.CalibrationInfo.Kg0.TopRight, mWiimoteState.BalanceBoardState.CalibrationInfo.Kg17.TopRight, mWiimoteState.BalanceBoardState.CalibrationInfo.Kg34.TopRight);
					mWiimoteState.BalanceBoardState.SensorValuesKg.BottomLeft = GetBalanceBoardSensorValue(mWiimoteState.BalanceBoardState.SensorValuesRaw.BottomLeft, mWiimoteState.BalanceBoardState.CalibrationInfo.Kg0.BottomLeft, mWiimoteState.BalanceBoardState.CalibrationInfo.Kg17.BottomLeft, mWiimoteState.BalanceBoardState.CalibrationInfo.Kg34.BottomLeft);
					mWiimoteState.BalanceBoardState.SensorValuesKg.BottomRight = GetBalanceBoardSensorValue(mWiimoteState.BalanceBoardState.SensorValuesRaw.BottomRight, mWiimoteState.BalanceBoardState.CalibrationInfo.Kg0.BottomRight, mWiimoteState.BalanceBoardState.CalibrationInfo.Kg17.BottomRight, mWiimoteState.BalanceBoardState.CalibrationInfo.Kg34.BottomRight);

					mWiimoteState.BalanceBoardState.SensorValuesLb.TopLeft = (mWiimoteState.BalanceBoardState.SensorValuesKg.TopLeft * KG2LB);
					mWiimoteState.BalanceBoardState.SensorValuesLb.TopRight = (mWiimoteState.BalanceBoardState.SensorValuesKg.TopRight * KG2LB);
					mWiimoteState.BalanceBoardState.SensorValuesLb.BottomLeft = (mWiimoteState.BalanceBoardState.SensorValuesKg.BottomLeft * KG2LB);
					mWiimoteState.BalanceBoardState.SensorValuesLb.BottomRight = (mWiimoteState.BalanceBoardState.SensorValuesKg.BottomRight * KG2LB);

					mWiimoteState.BalanceBoardState.WeightKg = (mWiimoteState.BalanceBoardState.SensorValuesKg.TopLeft + mWiimoteState.BalanceBoardState.SensorValuesKg.TopRight + mWiimoteState.BalanceBoardState.SensorValuesKg.BottomLeft + mWiimoteState.BalanceBoardState.SensorValuesKg.BottomRight) / 4.0f;
					mWiimoteState.BalanceBoardState.WeightLb = (mWiimoteState.BalanceBoardState.SensorValuesLb.TopLeft + mWiimoteState.BalanceBoardState.SensorValuesLb.TopRight + mWiimoteState.BalanceBoardState.SensorValuesLb.BottomLeft + mWiimoteState.BalanceBoardState.SensorValuesLb.BottomRight) / 4.0f;

					float Kx = (mWiimoteState.BalanceBoardState.SensorValuesKg.TopLeft + mWiimoteState.BalanceBoardState.SensorValuesKg.BottomLeft) / (mWiimoteState.BalanceBoardState.SensorValuesKg.TopRight + mWiimoteState.BalanceBoardState.SensorValuesKg.BottomRight);
					float Ky = (mWiimoteState.BalanceBoardState.SensorValuesKg.TopLeft + mWiimoteState.BalanceBoardState.SensorValuesKg.TopRight) / (mWiimoteState.BalanceBoardState.SensorValuesKg.BottomLeft + mWiimoteState.BalanceBoardState.SensorValuesKg.BottomRight);

					mWiimoteState.BalanceBoardState.CenterOfGravity.X = ((float)(Kx - 1) / (float)(Kx + 1)) * (float)(-BSL / 2);
					mWiimoteState.BalanceBoardState.CenterOfGravity.Y = ((float)(Ky - 1) / (float)(Ky + 1)) * (float)(-BSW / 2);
					break;
			}
		}

		private float GetBalanceBoardSensorValue(short sensor, short min, short mid, short max)
		{
			if(max == mid || mid == min)
				return 0;

			if(sensor < mid)
				return 68.0f * ((float)(sensor - min) / (mid - min));
			else
				return 68.0f * ((float)(sensor - mid) / (max - mid)) + 68.0f;
		}


		/// <summary>
		/// Parse data returned from a read report
		/// </summary>
		/// <param name="buff">Data buffer</param>
		private void ParseReadData(byte[] buff)
		{
			if((buff[3] & 0x08) != 0)
				throw new WiimoteException("Error reading data from Wiimote: Bytes do not exist.");

			if((buff[3] & 0x07) != 0)
				throw new WiimoteException("Error reading data from Wiimote: Attempt to read from write-only registers.");

			// get our size and offset from the report
			int size = (buff[3] >> 4) + 1;
			int offset = (buff[4] << 8 | buff[5]);

			// add it to the buffer
			Array.Copy(buff, 6, mReadBuff, offset - mAddress, size);

			// if we've read it all, set the event
			if(mAddress + mSize == offset + size)
				mReadDone.Set();
		}

		/// <summary>
		/// Returns whether rumble is currently enabled.
		/// </summary>
		/// <returns>Byte indicating true (0x01) or false (0x00)</returns>
		private byte GetRumbleBit()
		{
			return (byte)(mWiimoteState.Rumble ? 0x01 : 0x00);
		}

		/// <summary>
		/// Read calibration information stored on Wiimote
		/// </summary>
		private void ReadWiimoteCalibration()
		{
			// this appears to change the report type to 0x31
			byte[] buff = ReadData(0x0016, 7);

			mWiimoteState.AccelCalibrationInfo.X0 = buff[0];
			mWiimoteState.AccelCalibrationInfo.Y0 = buff[1];
			mWiimoteState.AccelCalibrationInfo.Z0 = buff[2];
			mWiimoteState.AccelCalibrationInfo.XG = buff[4];
			mWiimoteState.AccelCalibrationInfo.YG = buff[5];
			mWiimoteState.AccelCalibrationInfo.ZG = buff[6];
		}

		/// <summary>
		/// Set Wiimote reporting mode (if using an IR report type, IR sensitivity is set to WiiLevel3)
		/// </summary>
		/// <param name="type">Report type</param>
		/// <param name="continuous">Continuous data</param>
		public void SetReportType(InputReport type, bool continuous)
		{
			SetReportType(type, IRSensitivity.Maximum, continuous);
		}

		/// <summary>
		/// Set Wiimote reporting mode
		/// </summary>
		/// <param name="type">Report type</param>
		/// <param name="irSensitivity">IR sensitivity</param>
		/// <param name="continuous">Continuous data</param>
		public void SetReportType(InputReport type, IRSensitivity irSensitivity, bool continuous)
		{
			// only 1 report type allowed for the BB
			if(mWiimoteState.ExtensionType == ExtensionType.BalanceBoard)
				type = InputReport.ButtonsExtension;

			switch(type)
			{
				case InputReport.IRAccel:
					EnableIR(IRMode.Extended, irSensitivity);
					break;
				case InputReport.IRExtensionAccel:
					EnableIR(IRMode.Basic, irSensitivity);
					break;
				default:
					DisableIR();
					break;
			}

			ClearReport();
			mBuff[0] = (byte)OutputReport.Type;
			mBuff[1] = (byte)((continuous ? 0x04 : 0x00) | (byte)(mWiimoteState.Rumble ? 0x01 : 0x00));
			mBuff[2] = (byte)type;

			WriteReport();
		}

		/// <summary>
		/// Set the LEDs on the Wiimote
		/// </summary>
		/// <param name="led1">LED 1</param>
		/// <param name="led2">LED 2</param>
		/// <param name="led3">LED 3</param>
		/// <param name="led4">LED 4</param>
		public void SetLEDs(bool led1, bool led2, bool led3, bool led4)
		{
			mWiimoteState.LEDState.LED1 = led1;
			mWiimoteState.LEDState.LED2 = led2;
			mWiimoteState.LEDState.LED3 = led3;
			mWiimoteState.LEDState.LED4 = led4;

			ClearReport();

			mBuff[0] = (byte)OutputReport.LEDs;
			mBuff[1] =	(byte)(
						(led1 ? 0x10 : 0x00) |
						(led2 ? 0x20 : 0x00) |
						(led3 ? 0x40 : 0x00) |
						(led4 ? 0x80 : 0x00) |
						GetRumbleBit());

			WriteReport();
		}

		/// <summary>
		/// Set the LEDs on the Wiimote
		/// </summary>
		/// <param name="leds">The value to be lit up in base2 on the Wiimote</param>
		public void SetLEDs(int leds)
		{
			mWiimoteState.LEDState.LED1 = (leds & 0x01) > 0;
			mWiimoteState.LEDState.LED2 = (leds & 0x02) > 0;
			mWiimoteState.LEDState.LED3 = (leds & 0x04) > 0;
			mWiimoteState.LEDState.LED4 = (leds & 0x08) > 0;

			ClearReport();

			mBuff[0] = (byte)OutputReport.LEDs;
			mBuff[1] =	(byte)(
						((leds & 0x01) > 0 ? 0x10 : 0x00) |
						((leds & 0x02) > 0 ? 0x20 : 0x00) |
						((leds & 0x04) > 0 ? 0x40 : 0x00) |
						((leds & 0x08) > 0 ? 0x80 : 0x00) |
						GetRumbleBit());

			WriteReport();
		}

		/// <summary>
		/// Toggle rumble
		/// </summary>
		/// <param name="on">On or off</param>
		public void SetRumble(bool on)
		{
			mWiimoteState.Rumble = on;

			// the LED report also handles rumble
			SetLEDs(mWiimoteState.LEDState.LED1, 
					mWiimoteState.LEDState.LED2,
					mWiimoteState.LEDState.LED3,
					mWiimoteState.LEDState.LED4);
		}

		/// <summary>
		/// Retrieve the current status of the Wiimote and extensions.  Replaces GetBatteryLevel() since it was poorly named.
		/// </summary>
		public void GetStatus()
		{
			ClearReport();

			mBuff[0] = (byte)OutputReport.Status;
			mBuff[1] = GetRumbleBit();

			WriteReport();

			// signal the status report finished
			if(!mStatusDone.WaitOne(3000, false))
				throw new WiimoteException("Timed out waiting for status report");
		}

		/// <summary>
		/// Turn on the IR sensor
		/// </summary>
		/// <param name="mode">The data report mode</param>
		/// <param name="irSensitivity">IR sensitivity</param>
		private void EnableIR(IRMode mode, IRSensitivity irSensitivity)
		{
			mWiimoteState.IRState.Mode = mode;

			ClearReport();
			mBuff[0] = (byte)OutputReport.IR;
			mBuff[1] = (byte)(0x04 | GetRumbleBit());
			WriteReport();

			ClearReport();
			mBuff[0] = (byte)OutputReport.IR2;
			mBuff[1] = (byte)(0x04 | GetRumbleBit());
			WriteReport();

			WriteData(REGISTER_IR, 0x08);
			switch(irSensitivity)
			{
				case IRSensitivity.WiiLevel1:
					WriteData(REGISTER_IR_SENSITIVITY_1, 9, new byte[] {0x02, 0x00, 0x00, 0x71, 0x01, 0x00, 0x64, 0x00, 0xfe});
					WriteData(REGISTER_IR_SENSITIVITY_2, 2, new byte[] {0xfd, 0x05});
					break;
				case IRSensitivity.WiiLevel2:
					WriteData(REGISTER_IR_SENSITIVITY_1, 9, new byte[] {0x02, 0x00, 0x00, 0x71, 0x01, 0x00, 0x96, 0x00, 0xb4});
					WriteData(REGISTER_IR_SENSITIVITY_2, 2, new byte[] {0xb3, 0x04});
					break;
				case IRSensitivity.WiiLevel3:
					WriteData(REGISTER_IR_SENSITIVITY_1, 9, new byte[] {0x02, 0x00, 0x00, 0x71, 0x01, 0x00, 0xaa, 0x00, 0x64});
					WriteData(REGISTER_IR_SENSITIVITY_2, 2, new byte[] {0x63, 0x03});
					break;
				case IRSensitivity.WiiLevel4:
					WriteData(REGISTER_IR_SENSITIVITY_1, 9, new byte[] {0x02, 0x00, 0x00, 0x71, 0x01, 0x00, 0xc8, 0x00, 0x36});
					WriteData(REGISTER_IR_SENSITIVITY_2, 2, new byte[] {0x35, 0x03});
					break;
				case IRSensitivity.WiiLevel5:
					WriteData(REGISTER_IR_SENSITIVITY_1, 9, new byte[] {0x07, 0x00, 0x00, 0x71, 0x01, 0x00, 0x72, 0x00, 0x20});
					WriteData(REGISTER_IR_SENSITIVITY_2, 2, new byte[] {0x1, 0x03});
					break;
				case IRSensitivity.Maximum:
					WriteData(REGISTER_IR_SENSITIVITY_1, 9, new byte[] {0x02, 0x00, 0x00, 0x71, 0x01, 0x00, 0x90, 0x00, 0x41});
					WriteData(REGISTER_IR_SENSITIVITY_2, 2, new byte[] {0x40, 0x00});
					break;
				default:
					throw new ArgumentOutOfRangeException("irSensitivity");
			}
			WriteData(REGISTER_IR_MODE, (byte)mode);
			WriteData(REGISTER_IR, 0x08);
		}

		/// <summary>
		/// Disable the IR sensor
		/// </summary>
		private void DisableIR()
		{
			mWiimoteState.IRState.Mode = IRMode.Off;

			ClearReport();
			mBuff[0] = (byte)OutputReport.IR;
			mBuff[1] = GetRumbleBit();
			WriteReport();

			ClearReport();
			mBuff[0] = (byte)OutputReport.IR2;
			mBuff[1] = GetRumbleBit();
			WriteReport();
		}

		/// <summary>
		/// Initialize the report data buffer
		/// </summary>
		private void ClearReport()
		{
			Array.Clear(mBuff, 0, REPORT_LENGTH);
		}

		/// <summary>
		/// Write a report to the Wiimote
		/// </summary>
		private void WriteReport()
		{
			Debug.WriteLine("WriteReport: " + mBuff[0].ToString("x"));
			if(mAltWriteMethod)
				HIDImports.HidD_SetOutputReport(this.mHandle.DangerousGetHandle(), mBuff, (uint)mBuff.Length);
			else if(mStream != null)
				mStream.Write(mBuff, 0, REPORT_LENGTH);

			if(mBuff[0] == (byte)OutputReport.WriteMemory)
			{
				Debug.WriteLine("Wait");
				if(!mWriteDone.WaitOne(1000, false))
					Debug.WriteLine("Wait failed");
				//throw new WiimoteException("Error writing data to Wiimote...is it connected?");
			}
		}

		/// <summary>
		/// Read data or register from Wiimote
		/// </summary>
		/// <param name="address">Address to read</param>
		/// <param name="size">Length to read</param>
		/// <returns>Data buffer</returns>
		public byte[] ReadData(int address, short size)
		{
			ClearReport();

			mReadBuff = new byte[size];
			mAddress = address & 0xffff;
			mSize = size;

			mBuff[0] = (byte)OutputReport.ReadMemory;
			mBuff[1] = (byte)(((address & 0xff000000) >> 24) | GetRumbleBit());
			mBuff[2] = (byte)((address & 0x00ff0000)  >> 16);
			mBuff[3] = (byte)((address & 0x0000ff00)  >>  8);
			mBuff[4] = (byte)(address & 0x000000ff);

			mBuff[5] = (byte)((size & 0xff00) >> 8);
			mBuff[6] = (byte)(size & 0xff);

			WriteReport();

			if(!mReadDone.WaitOne(1000, false))
				throw new WiimoteException("Error reading data from Wiimote...is it connected?");

			return mReadBuff;
		}

		/// <summary>
		/// Write a single byte to the Wiimote
		/// </summary>
		/// <param name="address">Address to write</param>
		/// <param name="data">Byte to write</param>
		public void WriteData(int address, byte data)
		{
			WriteData(address, 1, new byte[] { data });
		}

		/// <summary>
		/// Write a byte array to a specified address
		/// </summary>
		/// <param name="address">Address to write</param>
		/// <param name="size">Length of buffer</param>
		/// <param name="buff">Data buffer</param>
		
		public void WriteData(int address, byte size, byte[] buff)
		{
			ClearReport();

			mBuff[0] = (byte)OutputReport.WriteMemory;
			mBuff[1] = (byte)(((address & 0xff000000) >> 24) | GetRumbleBit());
			mBuff[2] = (byte)((address & 0x00ff0000)  >> 16);
			mBuff[3] = (byte)((address & 0x0000ff00)  >>  8);
			mBuff[4] = (byte)(address & 0x000000ff);
			mBuff[5] = size;
			Array.Copy(buff, 0, mBuff, 6, size);

			WriteReport();
		}

		/// <summary>
		/// Current Wiimote state
		/// </summary>
		public WiimoteState WiimoteState
		{
			get { return mWiimoteState; }
		}

		///<summary>
		/// Unique identifier for this Wiimote (not persisted across application instances)
		///</summary>
		public Guid ID
		{
			get { return mID; }
		}

		/// <summary>
		/// HID device path for this Wiimote (valid until Wiimote is disconnected)
		/// </summary>
		public string HIDDevicePath
		{
			get { return mDevicePath; }
		}

		#region IDisposable Members

		/// <summary>
		/// Dispose Wiimote
		/// </summary>
		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		/// <summary>
		/// Dispose wiimote
		/// </summary>
		/// <param name="disposing">Disposing?</param>
		protected virtual void Dispose(bool disposing)
		{
			// close up our handles
			if(disposing)
				Disconnect();
		}
		#endregion
	}

	/// <summary>
	/// Thrown when no Wiimotes are found in the HID device list
	/// </summary>
	[Serializable]
	public class WiimoteNotFoundException : ApplicationException
	{
		/// <summary>
		/// Default constructor
		/// </summary>
		public WiimoteNotFoundException()
		{
		}

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="message">Error message</param>
		public WiimoteNotFoundException(string message) : base(message)
		{
		}

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="message">Error message</param>
		/// <param name="innerException">Inner exception</param>
		public WiimoteNotFoundException(string message, Exception innerException) : base(message, innerException)
		{
		}

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="info">Serialization info</param>
		/// <param name="context">Streaming context</param>
		protected WiimoteNotFoundException(SerializationInfo info, StreamingContext context) : base(info, context)
		{
		}
	}

	/// <summary>
	/// Represents errors that occur during the execution of the Wiimote library
	/// </summary>
	[Serializable]
	public class WiimoteException : ApplicationException
	{
		/// <summary>
		/// Default constructor
		/// </summary>
		public WiimoteException()
		{
		}

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="message">Error message</param>
		public WiimoteException(string message) : base(message)
		{
		}

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="message">Error message</param>
		/// <param name="innerException">Inner exception</param>
		public WiimoteException(string message, Exception innerException) : base(message, innerException)
		{
		}

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="info">Serialization info</param>
		/// <param name="context">Streaming context</param>
		protected WiimoteException(SerializationInfo info, StreamingContext context) : base(info, context)
		{
		}
	}

	/// <summary>
	/// Used to manage multiple Wiimotes
	/// </summary>
	public class WiimoteCollection : Collection<Wiimote>
	{
		/// <summary>
		/// Finds all Wiimotes connected to the system and adds them to the collection
		/// </summary>
		public void FindAllWiimotes()
		{
			Wiimote.FindWiimote(WiimoteFound);
		}

		private bool WiimoteFound(string devicePath)
		{
			this.Add(new Wiimote(devicePath));
			return true;
		}
	}

	/// <summary>
	/// Argument sent through the WiimoteExtensionChangedEvent
	/// </summary>
	public class WiimoteExtensionChangedEventArgs: EventArgs
	{
		/// <summary>
		/// The extenstion type inserted or removed
		/// </summary>
		public ExtensionType ExtensionType;
		/// <summary>
		/// Whether the extension was inserted or removed
		/// </summary>
		public bool Inserted;

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="type">The extension type inserted or removed</param>
		/// <param name="inserted">Whether the extension was inserted or removed</param>
		public WiimoteExtensionChangedEventArgs(ExtensionType type, bool inserted)
		{
			ExtensionType = type;
			Inserted = inserted;
		}
	}

	/// <summary>
	/// Argument sent through the WiimoteChangedEvent
	/// </summary>
	public class WiimoteChangedEventArgs: EventArgs
	{
		/// <summary>
		/// The current state of the Wiimote and extension controllers
		/// </summary>
		public WiimoteState WiimoteState;

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="ws">Wiimote state</param>
		public WiimoteChangedEventArgs(WiimoteState ws)
		{
			WiimoteState = ws;
		}
	}

#if MSRS
	[DataContract]
	public struct RumbleRequest
	{
		[DataMember]
		public bool Rumble;
	}
#endif

	/// <summary>
	/// Point structure for floating point 2D positions (X, Y)
	/// </summary>
	[Serializable]
	[DataContract]
	public struct PointF
	{
		/// <summary>
		/// X, Y coordinates of this point
		/// </summary>
		[DataMember]
		public float X, Y;

		/// <summary>
		/// Convert to human-readable string
		/// </summary>
		/// <returns>A string that represents the point</returns>
		public override string ToString()
		{
			return string.Format("{{X={0}, Y={1}}}", X, Y);
		}
		
	}

	/// <summary>
	/// Point structure for int 2D positions (X, Y)
	/// </summary>
	[Serializable]	
	[DataContract]
	public struct Point
	{
		/// <summary>
		/// X, Y coordinates of this point
		/// </summary>
		[DataMember]
		public int X, Y;

		/// <summary>
		/// Convert to human-readable string
		/// </summary>
		/// <returns>A string that represents the point.</returns>
		public override string ToString()
		{
			return string.Format("{{X={0}, Y={1}}}", X, Y);
		}
	}

	/// <summary>
	/// Point structure for floating point 3D positions (X, Y, Z)
	/// </summary>
	[Serializable]	
	[DataContract]
	public struct Point3F
	{
		/// <summary>
		/// X, Y, Z coordinates of this point
		/// </summary>
		[DataMember]
		public float X, Y, Z;

		/// <summary>
		/// Convert to human-readable string
		/// </summary>
		/// <returns>A string that represents the point</returns>
		public override string ToString()
		{
			return string.Format("{{X={0}, Y={1}, Z={2}}}", X, Y, Z);
		}
		
	}

	/// <summary>
	/// Point structure for int 3D positions (X, Y, Z)
	/// </summary>
	[Serializable]
	[DataContract]
	public struct Point3
	{
		/// <summary>
		/// X, Y, Z coordinates of this point
		/// </summary>
		[DataMember]
		public int X, Y, Z;

		/// <summary>
		/// Convert to human-readable string
		/// </summary>
		/// <returns>A string that represents the point.</returns>
		public override string ToString()
		{
			return string.Format("{{X={0}, Y={1}, Z={2}}}", X, Y, Z);
		}
	}

	/// <summary>
	/// Current overall state of the Wiimote and all attachments
	/// </summary>
	[Serializable]
	[DataContract]
	public class WiimoteState
	{
		/// <summary>
		/// Current calibration information
		/// </summary>
		[DataMember]
		public AccelCalibrationInfo AccelCalibrationInfo;
		/// <summary>
		/// Current state of accelerometers
		/// </summary>
		[DataMember]
		public AccelState AccelState;
		/// <summary>
		/// Current state of buttons
		/// </summary>
		[DataMember]
		public ButtonState ButtonState;
		/// <summary>
		/// Current state of IR sensors
		/// </summary>
		[DataMember]
		public IRState IRState;
		/// <summary>
		/// Raw byte value of current battery level
		/// </summary>
		[DataMember]
		public byte BatteryRaw;
		/// <summary>
		/// Calculated current battery level
		/// </summary>
		[DataMember]
		public float Battery;
		/// <summary>
		/// Current state of rumble
		/// </summary>
		[DataMember]
		public bool Rumble;
		/// <summary>
		/// Is an extension controller inserted?
		/// </summary>
		[DataMember]
		public bool Extension;
		/// <summary>
		/// Extension controller currently inserted, if any
		/// </summary>
		[DataMember]
		public ExtensionType ExtensionType;
		/// <summary>
		/// Current state of Nunchuk extension
		/// </summary>
		[DataMember]
		public NunchukState NunchukState;
		/// <summary>
		/// Current state of Classic Controller extension
		/// </summary>
		[DataMember]
		public ClassicControllerState ClassicControllerState;
		/// <summary>
		/// Current state of Guitar extension
		/// </summary>
		[DataMember]
		public GuitarState GuitarState;
		/// <summary>
		/// Current state of Drums extension
		/// </summary>
		[DataMember]
		public DrumsState DrumsState;
		/// <summary>
		/// Current state of the Wii Fit Balance Board
		/// </summary>
		public BalanceBoardState BalanceBoardState;
		/// <summary>
		/// Current state of LEDs
		/// </summary>
		[DataMember]
		public LEDState LEDState;

		/// <summary>
		/// Constructor for WiimoteState class
		/// </summary>
		public WiimoteState()
		{
			IRState.IRSensors = new IRSensor[4];
		}
	}

	/// <summary>
	/// Current state of LEDs
	/// </summary>
	[Serializable]
	[DataContract]
	public struct LEDState
	{
		/// <summary>
		/// LED on the Wiimote
		/// </summary>
		[DataMember]
		public bool LED1, LED2, LED3, LED4;
	}

	/// <summary>
	/// Calibration information stored on the Nunchuk
	/// </summary>
	[Serializable]
	[DataContract]
	public struct NunchukCalibrationInfo
	{
		/// <summary>
		/// Accelerometer calibration data
		/// </summary>
		public AccelCalibrationInfo AccelCalibration;
		/// <summary>
		/// Joystick X-axis calibration
		/// </summary>
		[DataMember]
		public byte MinX, MidX, MaxX;
		/// <summary>
		/// Joystick Y-axis calibration
		/// </summary>
		[DataMember]
		public byte MinY, MidY, MaxY;
	}

	/// <summary>
	/// Calibration information stored on the Classic Controller
	/// </summary>
	[Serializable]
	[DataContract]	
	public struct ClassicControllerCalibrationInfo
	{
		/// <summary>
		/// Left joystick X-axis 
		/// </summary>
		[DataMember]
		public byte MinXL, MidXL, MaxXL;
		/// <summary>
		/// Left joystick Y-axis
		/// </summary>
		[DataMember]
		public byte MinYL, MidYL, MaxYL;
		/// <summary>
		/// Right joystick X-axis
		/// </summary>
		[DataMember]
		public byte MinXR, MidXR, MaxXR;
		/// <summary>
		/// Right joystick Y-axis
		/// </summary>
		[DataMember]
		public byte MinYR, MidYR, MaxYR;
		/// <summary>
		/// Left analog trigger
		/// </summary>
		[DataMember]
		public byte MinTriggerL, MaxTriggerL;
		/// <summary>
		/// Right analog trigger
		/// </summary>
		[DataMember]
		public byte MinTriggerR, MaxTriggerR;
	}

	/// <summary>
	/// Current state of the Nunchuk extension
	/// </summary>
	[Serializable]
	[DataContract]	
	public struct NunchukState
	{
		/// <summary>
		/// Calibration data for Nunchuk extension
		/// </summary>
		[DataMember]
		public NunchukCalibrationInfo CalibrationInfo;
		/// <summary>
		/// State of accelerometers
		/// </summary>
		[DataMember]
		public AccelState AccelState;
		/// <summary>
		/// Raw joystick position before normalization.  Values range between 0 and 255.
		/// </summary>
		[DataMember]
		public Point RawJoystick;
		/// <summary>
		/// Normalized joystick position.  Values range between -0.5 and 0.5
		/// </summary>
		[DataMember]
		public PointF Joystick;
		/// <summary>
		/// Digital button on Nunchuk extension
		/// </summary>
		[DataMember]
		public bool C, Z;
	}

	/// <summary>
	/// Curernt button state of the Classic Controller
	/// </summary>
	[Serializable]
	[DataContract]
	public struct ClassicControllerButtonState
	{
		/// <summary>
		/// Digital button on the Classic Controller extension
		/// </summary>
		[DataMember]
		public bool A, B, Plus, Home, Minus, Up, Down, Left, Right, X, Y, ZL, ZR;
		/// <summary>
		/// Analog trigger - false if released, true for any pressure applied
		/// </summary>
		[DataMember]
		public bool TriggerL, TriggerR;
	}

	/// <summary>
	/// Current state of the Classic Controller
	/// </summary>
	[Serializable]
	[DataContract]
	public struct ClassicControllerState
	{
		/// <summary>
		/// Calibration data for Classic Controller extension
		/// </summary>
		[DataMember]
		public ClassicControllerCalibrationInfo CalibrationInfo;
		/// <summary>
		/// Current button state
		/// </summary>
		[DataMember]
		public ClassicControllerButtonState ButtonState;
		/// <summary>
		/// Raw value of left joystick.  Values range between 0 - 255.
		/// </summary>
		[DataMember]
		public Point RawJoystickL;
		/// <summary>
		/// Raw value of right joystick.  Values range between 0 - 255.
		/// </summary>
		[DataMember]
		public Point RawJoystickR;
		/// <summary>
		/// Normalized value of left joystick.  Values range between -0.5 - 0.5
		/// </summary>
		[DataMember]
		public PointF JoystickL;
		/// <summary>
		/// Normalized value of right joystick.  Values range between -0.5 - 0.5
		/// </summary>
		[DataMember]
		public PointF JoystickR;
		/// <summary>
		/// Raw value of analog trigger.  Values range between 0 - 255.
		/// </summary>
		[DataMember]
		public byte RawTriggerL, RawTriggerR;
		/// <summary>
		/// Normalized value of analog trigger.  Values range between 0.0 - 1.0.
		/// </summary>
		[DataMember]
		public float TriggerL, TriggerR;
	}

	/// <summary>
	/// Current state of the Guitar controller
	/// </summary>
	[Serializable]
	[DataContract]
	public struct GuitarState
	{
		/// <summary>
		/// Guitar type
		/// </summary>
		[DataMember]
		public GuitarType GuitarType;
		/// <summary>
		/// Current button state of the Guitar
		/// </summary>
		[DataMember]
		public GuitarButtonState ButtonState;
		/// <summary>
		/// Current fret button state of the Guitar
		/// </summary>
		[DataMember]
		public GuitarFretButtonState FretButtonState;
		/// <summary>
		/// Current touchbar state of the Guitar
		/// </summary>
		[DataMember]
		public GuitarFretButtonState TouchbarState;
		/// <summary>
		/// Raw joystick position.  Values range between 0 - 63.
		/// </summary>
		[DataMember]
		public Point RawJoystick;
		/// <summary>
		/// Normalized value of joystick position.  Values range between 0.0 - 1.0.
		/// </summary>
		[DataMember]
		public PointF Joystick;
		/// <summary>
		/// Raw whammy bar position.  Values range between 0 - 10.
		/// </summary>
		[DataMember]
		public byte RawWhammyBar;
		/// <summary>
		/// Normalized value of whammy bar position.  Values range between 0.0 - 1.0.
		/// </summary>
		[DataMember]
		public float WhammyBar;
	}

	/// <summary>
	/// Current fret button state of the Guitar controller
	/// </summary>
	[Serializable]
	[DataContract]
	public struct GuitarFretButtonState
	{
		/// <summary>
		/// Fret buttons
		/// </summary>
		[DataMember]
		public bool Green, Red, Yellow, Blue, Orange;
	}

	
	/// <summary>
	/// Current button state of the Guitar controller
	/// </summary>
	[Serializable]
	[DataContract]
	public struct GuitarButtonState
	{
		/// <summary>
		/// Strum bar
		/// </summary>
		[DataMember]
		public bool StrumUp, StrumDown;
		/// <summary>
		/// Other buttons
		/// </summary>
		[DataMember]
		public bool Minus, Plus;
	}

	/// <summary>
	/// Current state of the Drums controller
	/// </summary>
	[Serializable]
	[DataContract]
	public struct DrumsState
	{
		/// <summary>
		/// Drum pads
		/// </summary>
		public bool Red, Green, Blue, Orange, Yellow, Pedal;
		/// <summary>
		/// Speed at which the pad is hit.  Values range from 0 (very hard) to 6 (very soft)
		/// </summary>
		public int RedVelocity, GreenVelocity, BlueVelocity, OrangeVelocity, YellowVelocity, PedalVelocity;
		/// <summary>
		/// Other buttons
		/// </summary>
		public bool Plus, Minus;
		/// <summary>
		/// Raw value of analong joystick.  Values range from 0 - 15
		/// </summary>
		public Point RawJoystick;
		/// <summary>
		/// Normalized value of analog joystick.  Values range from 0.0 - 1.0
		/// </summary>
		public PointF Joystick;
	}

	/// <summary>
	/// Current state of the Wii Fit Balance Board controller
	/// </summary>
	[Serializable]
	[DataContract]
	public struct BalanceBoardState
	{
		/// <summary>
		/// Calibration information for the Balance Board
		/// </summary>
		[DataMember]
		public BalanceBoardCalibrationInfo CalibrationInfo;
		/// <summary>
		/// Raw values of each sensor
		/// </summary>
		[DataMember]
		public BalanceBoardSensors SensorValuesRaw;
		/// <summary>
		/// Kilograms per sensor
		/// </summary>
		[DataMember]
		public BalanceBoardSensorsF SensorValuesKg;
		/// <summary>
		/// Pounds per sensor
		/// </summary>
		[DataMember]
		public BalanceBoardSensorsF SensorValuesLb;
		/// <summary>
		/// Total kilograms on the Balance Board
		/// </summary>
		[DataMember]
		public float WeightKg;
		/// <summary>
		/// Total pounds on the Balance Board
		/// </summary>
		[DataMember]
		public float WeightLb;
		/// <summary>
		/// Center of gravity of Balance Board user
		/// </summary>
		[DataMember]
		public PointF CenterOfGravity;
	}

	/// <summary>
	/// Calibration information
	/// </summary>
	[Serializable]
	[DataContract]
	public struct BalanceBoardCalibrationInfo
	{
		/// <summary>
		/// Calibration information at 0kg
		/// </summary>
		[DataMember]
		public BalanceBoardSensors Kg0;
		/// <summary>
		/// Calibration information at 17kg
		/// </summary>
		[DataMember]
		public BalanceBoardSensors Kg17;
		/// <summary>
		/// Calibration information at 34kg
		/// </summary>
		[DataMember]
		public BalanceBoardSensors Kg34;
	}

	/// <summary>
	/// The 4 sensors on the Balance Board (short values)
	/// </summary>
	[Serializable]
	[DataContract]
	public struct BalanceBoardSensors
	{
		/// <summary>
		/// Sensor at top right
		/// </summary>
		[DataMember]
		public short TopRight;
		/// <summary>
		/// Sensor at top left
		/// </summary>
		[DataMember]
		public short TopLeft;
		/// <summary>
		/// Sensor at bottom right
		/// </summary>
		[DataMember]
		public short BottomRight;
		/// <summary>
		/// Sensor at bottom left
		/// </summary>
		[DataMember]
		public short BottomLeft;
	}

	/// <summary>
	/// The 4 sensors on the Balance Board (float values)
	/// </summary>
	[Serializable]
	[DataContract]
	public struct BalanceBoardSensorsF
	{
		/// <summary>
		/// Sensor at top right
		/// </summary>
		[DataMember]
		public float TopRight;
		/// <summary>
		/// Sensor at top left
		/// </summary>
		[DataMember]
		public float TopLeft;
		/// <summary>
		/// Sensor at bottom right
		/// </summary>
		[DataMember]
		public float BottomRight;
		/// <summary>
		/// Sensor at bottom left
		/// </summary>
		[DataMember]
		public float BottomLeft;
	}

	/// <summary>
	/// Current state of a single IR sensor
	/// </summary>
	[Serializable]
	[DataContract]
	public struct IRSensor
	{
		/// <summary>
		/// Raw values of individual sensor.  Values range between 0 - 1023 on the X axis and 0 - 767 on the Y axis.
		/// </summary>
		[DataMember]
		public Point RawPosition;
		/// <summary>
		/// Normalized values of the sensor position.  Values range between 0.0 - 1.0.
		/// </summary>
		[DataMember]
		public PointF Position;
		/// <summary>
		/// Size of IR Sensor.  Values range from 0 - 15
		/// </summary>
		[DataMember]
		public int Size;
		/// <summary>
		/// IR sensor seen
		/// </summary>
		[DataMember]
		public bool Found;
		/// <summary>
		/// Convert to human-readable string
		/// </summary>
		/// <returns>A string that represents the point.</returns>
		public override string ToString()
		{
			return string.Format("{{{0}, Size={1}, Found={2}}}", Position, Size, Found);
		}
	}

	/// <summary>
	/// Current state of the IR camera
	/// </summary>
	[Serializable]
	[DataContract]
	public struct IRState
	{
		/// <summary>
		/// Current mode of IR sensor data
		/// </summary>
		[DataMember]
		public IRMode Mode;
		/// <summary>
		/// Current state of IR sensors
		/// </summary>
		[DataMember]
		public IRSensor[] IRSensors;
		/// <summary>
		/// Raw midpoint of IR sensors 1 and 2 only.  Values range between 0 - 1023, 0 - 767
		/// </summary>
		[DataMember]
		public Point RawMidpoint;
		/// <summary>
		/// Normalized midpoint of IR sensors 1 and 2 only.  Values range between 0.0 - 1.0
		/// </summary>
		[DataMember]
		public PointF Midpoint;
	}

	/// <summary>
	/// Current state of the accelerometers
	/// </summary>
	[Serializable]
	[DataContract]
	public struct AccelState
	{
		/// <summary>
		/// Raw accelerometer data.
		/// <remarks>Values range between 0 - 255</remarks>
		/// </summary>
		[DataMember]
		public Point3 RawValues;
		/// <summary>
		/// Normalized accelerometer data.  Values range between 0 - ?, but values > 3 and &lt; -3 are inaccurate.
		/// </summary>
		[DataMember]
		public Point3F Values;
	}

	/// <summary>
	/// Accelerometer calibration information
	/// </summary>
	[Serializable]
	[DataContract]
	public struct AccelCalibrationInfo
	{
		/// <summary>
		/// Zero point of accelerometer
		/// </summary>
		[DataMember]
		public byte X0, Y0, Z0;
		/// <summary>
		/// Gravity at rest of accelerometer
		/// </summary>
		[DataMember]
		public byte XG, YG, ZG;
	}

	/// <summary>
	/// Current button state
	/// </summary>
	[Serializable]
	[DataContract]
	public struct ButtonState
	{
		/// <summary>
		/// Digital button on the Wiimote
		/// </summary>
		[DataMember]
		public bool A, B, Plus, Home, Minus, One, Two, Up, Down, Left, Right;
	}

	/// <summary>
	/// The extension plugged into the Wiimote
	/// </summary>
	[DataContract]
	public enum ExtensionType : long
	{
		/// <summary>
		/// No extension
		/// </summary>
		None				= 0x000000000000,
		/// <summary>
		/// Nunchuk extension
		/// </summary>
		Nunchuk				= 0x0000a4200000,
		NewNunchuk			= 0xff00a4200000,
		/// <summary>
		/// Classic Controller extension
		/// </summary>
		ClassicController   = 0x0000a4200101,
		ClassicControllerPro = 0x0100a4200101,
		/// <summary>
		/// Guitar controller from Guitar Hero 3/WorldTour
		/// </summary>
		Guitar				= 0x0000a4200103,
		/// <summary>
		/// Drum controller from Guitar Hero: World Tour
		/// </summary>
		Drums				= 0x0100a4200103,
		/// <summary>
		/// Wii Fit Balance Board controller
		/// </summary>
		BalanceBoard		= 0x0000a4200402,
		/// <summary>
		/// Partially inserted extension.  This is an error condition.
		/// </summary>
		ParitallyInserted	= 0xffffffffffff
	};

	/// <summary>
	/// The mode of data reported for the IR sensor
	/// </summary>
	[DataContract]
	public enum IRMode : byte
	{
		/// <summary>
		/// IR sensor off
		/// </summary>
		Off			= 0x00,
		/// <summary>
		/// Basic mode
		/// </summary>
		Basic		= 0x01,	// 10 bytes
		/// <summary>
		/// Extended mode
		/// </summary>
		Extended	= 0x03,	// 12 bytes
		/// <summary>
		/// Full mode (unsupported)
		/// </summary>
		Full		= 0x05,	// 16 bytes * 2 (format unknown)
	};

	/// <summary>
	/// The report format in which the Wiimote should return data
	/// </summary>
	public enum InputReport : byte
	{
		/// <summary>
		/// Status report
		/// </summary>
		Status				= 0x20,
		/// <summary>
		/// Read data from memory location
		/// </summary>
		ReadData			= 0x21,
		/// <summary>
		/// Register write complete
		/// </summary>
		OutputReportAck		= 0x22,
		/// <summary>
		/// Button data only
		/// </summary>
		Buttons				= 0x30,
		/// <summary>
		/// Button and accelerometer data
		/// </summary>
		ButtonsAccel		= 0x31,
		/// <summary>
		/// IR sensor and accelerometer data
		/// </summary>
		IRAccel				= 0x33,
		/// <summary>
		/// Button and extension controller data
		/// </summary>
		ButtonsExtension	= 0x34,
		/// <summary>
		/// Extension and accelerometer data
		/// </summary>
		ExtensionAccel		= 0x35,
		/// <summary>
		/// IR sensor, extension controller and accelerometer data
		/// </summary>
		IRExtensionAccel	= 0x37,
	};

	/// <summary>
	/// Sensitivity of the IR camera on the Wiimote
	/// </summary>

	public enum IRSensitivity
	{
		/// <summary>
		/// Equivalent to level 1 on the Wii console
		/// </summary>
		WiiLevel1,
		/// <summary>
		/// Equivalent to level 2 on the Wii console
		/// </summary>
		WiiLevel2,
		/// <summary>
		/// Equivalent to level 3 on the Wii console (default)
		/// </summary>
		WiiLevel3,
		/// <summary>
		/// Equivalent to level 4 on the Wii console
		/// </summary>
		WiiLevel4,
		/// <summary>
		/// Equivalent to level 5 on the Wii console
		/// </summary>
		WiiLevel5,
		/// <summary>
		/// Maximum sensitivity
		/// </summary>
		Maximum
	}

	/// <summary>
	/// Type of guitar extension: Guitar Hero 3 or Guitar Hero World Tour
	/// </summary>
	public enum GuitarType
	{
		/// <summary>
		///  Guitar Hero 3 guitar controller
		/// </summary>
		GuitarHero3,
		/// <summary>
		/// Guitar Hero: World Tour guitar controller
		/// </summary>
		GuitarHeroWorldTour
	}

	/// <summary>
	/// Win32 import information for use with the Wiimote library
	/// </summary>
	internal class HIDImports
	{
		//
		// Flags controlling what is included in the device information set built
		// by SetupDiGetClassDevs
		//
		public const int DIGCF_DEFAULT          = 0x00000001; // only valid with DIGCF_DEVICEINTERFACE
		public const int DIGCF_PRESENT          = 0x00000002;
		public const int DIGCF_ALLCLASSES       = 0x00000004;
		public const int DIGCF_PROFILE          = 0x00000008;
		public const int DIGCF_DEVICEINTERFACE  = 0x00000010;

		[Flags]
		public enum EFileAttributes : uint
		{
		   Readonly         = 0x00000001,
		   Hidden           = 0x00000002,
		   System           = 0x00000004,
		   Directory        = 0x00000010,
		   Archive          = 0x00000020,
		   Device           = 0x00000040,
		   Normal           = 0x00000080,
		   Temporary        = 0x00000100,
		   SparseFile       = 0x00000200,
		   ReparsePoint     = 0x00000400,
		   Compressed       = 0x00000800,
		   Offline          = 0x00001000,
		   NotContentIndexed= 0x00002000,
		   Encrypted        = 0x00004000,
		   Write_Through    = 0x80000000,
		   Overlapped       = 0x40000000,
		   NoBuffering      = 0x20000000,
		   RandomAccess     = 0x10000000,
		   SequentialScan   = 0x08000000,
		   DeleteOnClose    = 0x04000000,
		   BackupSemantics  = 0x02000000,
		   PosixSemantics   = 0x01000000,
		   OpenReparsePoint = 0x00200000,
		   OpenNoRecall     = 0x00100000,
		   FirstPipeInstance= 0x00080000
		}

		[StructLayout(LayoutKind.Sequential)]
		public struct SP_DEVINFO_DATA
		{
			public uint cbSize;
			public Guid ClassGuid;
			public uint DevInst;
			public IntPtr Reserved;
		}

		[StructLayout(LayoutKind.Sequential)]
		public struct SP_DEVICE_INTERFACE_DATA
		{
			public int cbSize;
			public Guid InterfaceClassGuid;
			public int Flags;
			public IntPtr RESERVED;
		}

		[StructLayout(LayoutKind.Sequential, Pack = 1)]
		public struct SP_DEVICE_INTERFACE_DETAIL_DATA
		{
			public UInt32 cbSize;
			[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
			public string DevicePath;
		}

		[StructLayout(LayoutKind.Sequential)]
		public struct HIDD_ATTRIBUTES
		{
			public int Size;
			public short VendorID;
			public short ProductID;
			public short VersionNumber;
		}

		[DllImport(@"hid.dll", CharSet=CharSet.Auto, SetLastError = true)]
		public static extern void HidD_GetHidGuid(out Guid gHid);

		[DllImport("hid.dll")]
		public static extern Boolean HidD_GetAttributes(IntPtr HidDeviceObject, ref HIDD_ATTRIBUTES Attributes);

		[DllImport("hid.dll")]
		internal extern static bool HidD_SetOutputReport(
			IntPtr HidDeviceObject,
			byte[] lpReportBuffer,
			uint ReportBufferLength);

		[DllImport(@"setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
		public static extern IntPtr SetupDiGetClassDevs(
			ref Guid ClassGuid,
			[MarshalAs(UnmanagedType.LPTStr)] string Enumerator,
			IntPtr hwndParent,
			UInt32 Flags
			);

		[DllImport(@"setupapi.dll", CharSet=CharSet.Auto, SetLastError = true)]
		public static extern Boolean SetupDiEnumDeviceInterfaces(
			IntPtr hDevInfo,
			//ref SP_DEVINFO_DATA devInfo,
			IntPtr devInvo,
			ref Guid interfaceClassGuid,
			Int32 memberIndex,
			ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData
		);

		[DllImport(@"setupapi.dll", SetLastError = true)]
		public static extern Boolean SetupDiGetDeviceInterfaceDetail(
			IntPtr hDevInfo,
			ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
			IntPtr deviceInterfaceDetailData,
			UInt32 deviceInterfaceDetailDataSize,
			out UInt32 requiredSize,
			IntPtr deviceInfoData
		);

		[DllImport(@"setupapi.dll", SetLastError = true)]
		public static extern Boolean SetupDiGetDeviceInterfaceDetail(
			IntPtr hDevInfo,
			ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
			ref SP_DEVICE_INTERFACE_DETAIL_DATA deviceInterfaceDetailData,
			UInt32 deviceInterfaceDetailDataSize,
			out UInt32 requiredSize,
			IntPtr deviceInfoData
		);

		[DllImport(@"setupapi.dll", CharSet=CharSet.Auto, SetLastError = true)]
		public static extern UInt16 SetupDiDestroyDeviceInfoList( IntPtr hDevInfo );

		[DllImport("Kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
		public static extern SafeFileHandle CreateFile(
			string fileName,
			[MarshalAs(UnmanagedType.U4)] FileAccess fileAccess,
			[MarshalAs(UnmanagedType.U4)] FileShare fileShare,
			IntPtr securityAttributes,
			[MarshalAs(UnmanagedType.U4)] FileMode creationDisposition,
			[MarshalAs(UnmanagedType.U4)] EFileAttributes flags,
			IntPtr template);

			[DllImport("kernel32.dll", SetLastError=true)]
			[return: MarshalAs(UnmanagedType.Bool)]
			public static extern bool CloseHandle(IntPtr hObject);
	}

	/// <summary>
	/// Class which waits for a new bluetooth device and registers it with the system (no pairing, just single connection)
	/// </summary>
	public class ConnectionManager
	{
		public static bool ElevateProcessNeedRestart()
		{
			bool IsAdmin = (new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
			if (IsAdmin) return false;
			Process proc = new Process();
			string SelfCommandline = Environment.CommandLine;
			int CmdSplit = SelfCommandline.IndexOf(SelfCommandline[0] == '"' ? '"' : ' ', 1);
			proc.StartInfo.FileName = (CmdSplit < 0 ? SelfCommandline : SelfCommandline.Substring((SelfCommandline[0] == '"' ? 1 : 0), CmdSplit - (SelfCommandline[0] == '"' ? 1 : 0)));
			proc.StartInfo.Arguments = (CmdSplit < 0 || CmdSplit + 1 == SelfCommandline.Length ? "" : SelfCommandline.Substring(CmdSplit + (SelfCommandline[0] == '"' ? 2 : 1)) + " ") + "-RESTARTELEVATED";
			proc.StartInfo.WorkingDirectory = Environment.CurrentDirectory;
			proc.StartInfo.Verb = "runas";
			try { if (!proc.Start()) throw new Exception(); }
			catch { throw new ApplicationException("Could not restart self"); }
			return true;
		}

		private bool bCancel = false, bDidConnect = false;
		private Thread Worker = null;

		public bool ConnectNextWiiMote()
		{
			if (IsRunning()) return true;

			////This check if bluetooth is available at all is too slow, instead wait for thread to set error state
			//BLUETOOTH_DEVICE_INFO dev = new BLUETOOTH_DEVICE_INFO();
			//dev.dwSize = Marshal.SizeOf(typeof(BLUETOOTH_DEVICE_INFO));
			//BLUETOOTH_DEVICE_SEARCH_PARAMS sp = new BLUETOOTH_DEVICE_SEARCH_PARAMS();
			//sp.dwSize = Marshal.SizeOf(typeof(BLUETOOTH_DEVICE_SEARCH_PARAMS));
			//sp.fIssueInquiry = sp.fReturnAuthenticated = sp.fReturnConnected = sp.fReturnRemembered = sp.fReturnUnknown = true;
			//sp.cTimeoutMultiplier = 1;
			//IntPtr handle = BluetoothFindFirstDevice(ref sp, ref dev);
			//int lasterror = Marshal.GetLastWin32Error();
			//if (handle == IntPtr.Zero && lasterror != ERROR_SUCCESS && lasterror != ERROR_NO_MORE_ITEMS) return false;
			//if (handle != IntPtr.Zero) BluetoothFindDeviceClose(handle);

			Worker = new Thread(new ThreadStart(delegate() { this.ThreadConnect(); }));
			bCancel = bDidConnect = false;
			Worker.Start();
			return true;
		}

		public bool IsRunning()  { return Worker != null && Worker.IsAlive; }
		public bool DidConnect() { return !IsRunning() && bDidConnect;  }
		public bool HadError()   { return !IsRunning() && !bDidConnect;  }
		public void Cancel()     { bCancel = IsRunning();  }

		private void ThreadConnect()
		{
			BLUETOOTH_DEVICE_INFO dev = new BLUETOOTH_DEVICE_INFO();
			dev.dwSize = Marshal.SizeOf(typeof(BLUETOOTH_DEVICE_INFO));

			BLUETOOTH_DEVICE_SEARCH_PARAMS sp = new BLUETOOTH_DEVICE_SEARCH_PARAMS();
			sp.dwSize = Marshal.SizeOf(typeof(BLUETOOTH_DEVICE_SEARCH_PARAMS));
			sp.fIssueInquiry = sp.fReturnAuthenticated = sp.fReturnConnected = sp.fReturnRemembered = sp.fReturnUnknown = true;
			sp.cTimeoutMultiplier = 1;

			bool HadError = false;
			while (!bCancel && !bDidConnect && !HadError)
			{
				IntPtr handle = BluetoothFindFirstDevice(ref sp, ref dev);
				if (handle == IntPtr.Zero)
				{
					int lasterror = Marshal.GetLastWin32Error();
					if (lasterror != ERROR_SUCCESS && lasterror != ERROR_NO_MORE_ITEMS) HadError = true;
					continue;
				}
				while (!bCancel && !bDidConnect && !HadError)
				{
					if (dev.szName.StartsWith("Nintendo RVL"))
					{
						if (dev.fRemembered)
						{
							BluetoothRemoveDevice(ref dev.Address);
						}
						else if (BluetoothSetServiceState(IntPtr.Zero, ref dev, ref HumanInterfaceDeviceServiceClass_UUID, BLUETOOTH_SERVICE_ENABLE) != 0)
						{
							int lasterror = Marshal.GetLastWin32Error();
							if (lasterror != ERROR_SUCCESS) HadError = true;
						}
						else
						{
							bDidConnect = true;
							break;
						}
					}

					dev.szName = "";
					if (!BluetoothFindNextDevice(handle, ref dev)) break;
				}
				BluetoothFindDeviceClose(handle);
			}
		}

		#region pinvoke defs
		[StructLayout(LayoutKind.Sequential)] struct SYSTEMTIME { public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds; }
		[StructLayout(LayoutKind.Sequential)] struct BLUETOOTH_ADDRESS { public byte byte1, byte2, byte3, byte4, byte5, byte6, bytex1, bytex2; }
		[StructLayout(LayoutKind.Sequential)] struct BLUETOOTH_DEVICE_SEARCH_PARAMS
		{
			public int dwSize;                // sizeof this structure
			public bool fReturnAuthenticated; // return authenticated devices
			public bool fReturnRemembered;    // return remembered devices
			public bool fReturnUnknown;       // return unknown devices
			public bool fReturnConnected;     // return connected devices
			public bool fIssueInquiry;        // issue a new inquiry
			public byte cTimeoutMultiplier;   // timeout for the inquiry
			public IntPtr hRadio;             // handle to radio to enumerate - NULL == all radios will be searched
		}
		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct BLUETOOTH_DEVICE_INFO
		{
			public int dwSize;                // size of this structure
			public int padding;               // padding
			public BLUETOOTH_ADDRESS Address; // Bluetooth address
			public uint ulClassofDevice;      // Bluetooth "Class of Device"
			public bool fConnected;           // Device connected/in use
			public bool fRemembered;          // Device remembered
			public bool fAuthenticated;       // Device authenticated/paired/bonded
			public SYSTEMTIME stLastSeen;     // Last time the device was seen
			public SYSTEMTIME stLastUsed;     // Last time the device was used for other than RNR, inquiry, or SDP
			[MarshalAs(UnmanagedType.ByValTStr, SizeConst = BLUETOOTH_MAX_NAME_SIZE)] public string szName; // Name of the device
		}
		[DllImport("bthprops.cpl", CharSet = CharSet.Auto, SetLastError = true)] static extern IntPtr BluetoothFindFirstDevice(ref BLUETOOTH_DEVICE_SEARCH_PARAMS SearchParams, ref BLUETOOTH_DEVICE_INFO DeviceInfo);
		[DllImport("bthprops.cpl", CharSet = CharSet.Auto, SetLastError = true)] static extern bool BluetoothFindNextDevice(IntPtr hFind, ref BLUETOOTH_DEVICE_INFO DeviceInfo);
		[DllImport("bthprops.cpl", CharSet = CharSet.Auto, SetLastError = true)] static extern bool BluetoothFindDeviceClose(IntPtr hFind);
		[DllImport("bthprops.cpl", CharSet = CharSet.Auto, SetLastError = true)] static extern uint BluetoothSetServiceState(IntPtr hRadio, ref BLUETOOTH_DEVICE_INFO DeviceInfo, ref Guid guid, int ServiceFlags);
		[DllImport("bthprops.cpl", CharSet = CharSet.Auto, SetLastError = true)] static extern uint BluetoothRemoveDevice(ref BLUETOOTH_ADDRESS Address);
		const int BLUETOOTH_MAX_NAME_SIZE = 248, BLUETOOTH_SERVICE_DISABLE = 0, BLUETOOTH_SERVICE_ENABLE = 1, ERROR_SUCCESS = 0, ERROR_NO_MORE_ITEMS = 259;
		Guid HumanInterfaceDeviceServiceClass_UUID = new Guid(0x00001124, 0x0000, 0x1000, 0x80, 0x00, 0x00, 0x80, 0x5F, 0x9B, 0x34, 0xFB);
		#endregion
	}
}
