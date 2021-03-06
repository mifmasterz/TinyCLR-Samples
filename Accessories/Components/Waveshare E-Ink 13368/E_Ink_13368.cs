﻿using GHIElectronics.TinyCLR.Devices.Gpio;
using GHIElectronics.TinyCLR.Devices.Spi;
using System;
using System.Threading;

namespace GHIElectronics.TinyCLR.Drivers.Waveshare {
    //With wires at bottom of display, bytes are vertically arranged starting at top left. MSb is top, LSb is bottom. 212 bytes across.
    //13 bytes vertically,
    //Resolution is 212 x 104.
    class E_Ink_13368 {
        //Derived from epd2in13b_V2.cpp
        private static GpioPin dc, busy;
        private static SpiDevice eInkSpi;
        private static int displayWidth = 212, displayHeight = 104;

        public enum EinkColor {
            Black = 0,
            Red = 1,
        }

        public E_Ink_13368(string spiBus, int csPin, int dcPin, int rstPin, int busyPin) {
            var cs = GpioController.GetDefault().OpenPin(csPin);
            cs.SetDriveMode(GpioPinDriveMode.Output);

            dc = GpioController.GetDefault().OpenPin(dcPin);
            dc.SetDriveMode(GpioPinDriveMode.Output);

            var rst = GpioController.GetDefault().OpenPin(rstPin);
            rst.SetDriveMode(GpioPinDriveMode.Output);

            busy = GpioController.GetDefault().OpenPin(busyPin);
            busy.SetDriveMode(GpioPinDriveMode.Input);

            var settings = new SpiConnectionSettings() {
                ChipSelectType = SpiChipSelectType.Gpio,
                ChipSelectLine = cs,
                Mode = SpiMode.Mode0,
                ClockFrequency = 2_000_000, //Can write @ 20 MHz, but read is only 2.5 Mhz.
                ChipSelectActiveState = false,
            };

            eInkSpi = SpiController.FromName(spiBus).GetDevice(settings);

            //Reset EPD driver IC.
            rst.Write(GpioPinValue.High);
            Thread.Sleep(100);
            rst.Write(GpioPinValue.Low);        //Rst is active low.
            Thread.Sleep(10);
            rst.Write(GpioPinValue.High);
            Thread.Sleep(100);

            cs.Write(GpioPinValue.Low);         //Assert CS (active low).
            waitWhileBusy();

            sendCommand(new byte[] { 0x04 });   //Power on.
            waitWhileBusy();

            sendCommand(new byte[] { 0x00 });   //Panel setting.
            sendData(new byte[] { 0x0F });
            sendData(new byte[] { 0x89 });

            sendCommand(new byte[] { 0x61 });
            sendData(new byte[] { 0x68, 0x00, 0xD4 });

            sendCommand(new byte[] { 0x50 });
            sendData(new byte[] { 0x77 });

            ClearScreen();
        }

        public void ClearScreen() {
            sendCommand(new byte[] { 0x10 });
            for (int i = 0; i < displayWidth * displayHeight >> 3; i++) {
                sendData(new byte[] { 0xFF });
            }

            sendCommand(new byte[] { 0x13 });
            for (int i = 0; i < displayWidth * displayHeight >> 3; i++) {
                sendData(new byte[] { 0xFF });
            }
        }

        public void SetPixel(int xCoord, int yCoord) {

        }

        public void DrawBuffer(byte[] buffer) {
            //For each pixel:
            //    If R<16 and G<32 and B<16 use black.
            //    If R>15 and G<32 and B<16 use red.
            //    All other pixels are white.

            //Display is 212 x 13 bytes (212 x 104).
            if (buffer.Length != ((displayWidth * displayHeight) << 1)) throw new ArgumentOutOfRangeException();

            //Do red first. Red always overwrites black -- order doesn't matter.
            sendCommand(new byte[] { 0x13 }); //Tell display data that follows is red.
            int x, y, yBase, red, green, blue, firstByte, secondByte;
            byte dataByte;

            //Todo: Get rid of duplicate code for red and black.
            for (int byteIndex = 0; byteIndex < displayWidth * displayHeight >> 3; byteIndex++) {
                dataByte = 0;
                x = displayWidth - 1 - byteIndex / 13;
                yBase = (byteIndex % 13) << 3;

                for (int bitIndex = 0; bitIndex < 8; bitIndex++) {
                    y = yBase + 7 - bitIndex;

                    firstByte = buffer[x * 2 + y * displayWidth * 2];
                    secondByte = buffer[x * 2 + y * displayWidth * 2 + 1];

                    red = secondByte >> 3;
                    green = firstByte >> 5 + (secondByte & 0b00000111) << 3;
                    blue = firstByte & 0b00011111;

                    if (red > 15 && green < 32 && blue < 16) {
                        dataByte += (byte)Math.Pow(2.0, bitIndex); //Todo: Don't use Math.Pow.
                    }
                }
                sendData(new byte[] { (byte)~dataByte });
            }

            //Do black.
            sendCommand(new byte[] { 0x10 }); //Tell display data that follows is black.

            for (int byteIndex = 0; byteIndex < displayWidth * displayHeight >> 3; byteIndex++) {
                dataByte = 0;
                x = displayWidth - 1 - byteIndex / 13;
                yBase = (byteIndex % 13) << 3;

                for (int bitIndex = 0; bitIndex < 8; bitIndex++) {
                    y = yBase + 7 - bitIndex;

                    firstByte = buffer[x * 2 + y * displayWidth * 2];
                    secondByte = buffer[x * 2 + y * displayWidth * 2 + 1];

                    red = secondByte >> 3;
                    green = firstByte >> 5 + (secondByte & 0b00000111) << 3;
                    blue = firstByte & 0b00011111;

                    if (red < 16 && green < 32 && blue < 16) {
                        dataByte += (byte)Math.Pow(2.0, bitIndex);
                    }
                }
                sendData(new byte[] { (byte)~dataByte });
            }
        }

        public void DrawNativeBuffer(byte[] buffer, EinkColor color) {
            if (buffer.Length != displayWidth * displayHeight >> 3) throw new ArgumentOutOfRangeException();

            sendCommand(new byte[] { color == EinkColor.Black ? (byte)0x10 : (byte)0x13 });

            for (int i = 0; i < buffer.Length; i++) {
                sendData(new byte[] { (byte)~buffer[i] });
            }
        }

        public void RefreshDisplay() {
            sendCommand(new byte[] { 0x12 });
            waitWhileBusy();
        }


        private void waitWhileBusy() {
            //Busy pin will sometimes go high for a very short duration when still busy. Wait until you get 5 "not busy" in a row.
            var count = 0;
            while (count < 5) {
                while (busy.Read() == GpioPinValue.Low) {
                    count = 0;
                    Thread.Sleep(20);
                }
                count++;
            }
        }

        private void sendCommand(byte[] data) {
            dc.Write(GpioPinValue.Low);         //Set command mode.
            eInkSpi.Write(data);
        }

        private void sendData(byte[] data) {
            dc.Write(GpioPinValue.High);        //Set data mode.
            eInkSpi.Write(data);
        }
    }
}
