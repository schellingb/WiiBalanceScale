# WiiBalanceScale
Use the Wii Balance Board as a pretty accurate weight scale (± 20 gram).

## Download
You can find a binary download under the [Releases page](https://github.com/schellingb/WiiBalanceScale/releases/latest).

## Requirements
- Wii Balance Board
- Windows
- Bluetooth (built-in or USB dongle)

## Usage
On startup, the application will try to find a connected Wii Balance Board. If none is connected it will start to scan for new Bluetooth devices. As soon as a Wii Balance Board is found (by pressing the SYNC button on the device) it will establish a connection to it.

The application will then quickly calculate the initial zeroing. At any time you can click the 'Zero' button to do manual zeroing of the scale.

Below the weight you can find a gauge indicating the quality/accuracy of the measurement. It goes from 0 stars (1 kg unit accuracy) to 5 stars (± 20 gram accuracy) after 5 seconds. The Wii Balance Board is not very accurate in an instant measure but it becomes more accurate by accumulating measurement over time.

Click the weight unit label to switch between kilogram (kg) and pounds (lbs).

![Screenshot](https://raw.githubusercontent.com/schellingb/WiiBalanceScale/master/README.png)

## Troubleshooting
The automatic connection to the Bluetooth device requires admin rights which the application tries to acquire automatically. If this does not work for you, you can launch the application by right-clicking it and selecting 'Run as administrator' or you can connect to it using the Windows Bluetooth control panel before launching the application.

## License
WiiBalanceScale and the included [WiimoteLib by BrianPeek](https://github.com/BrianPeek/WiimoteLib) are available under the [MIT license](https://choosealicense.com/licenses/mit/).
