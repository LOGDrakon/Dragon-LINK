# Dragon LINK

## At this moment
Work in progress!

The purpose of this project is to create a custom protocol to allow STM32-USB devices to exchange data with a PC.
At this moment, the PC software is only for Windows.

The Device-LINK package (which contains the STM32 base code with main commands) is not included yet; it will be added later.
This repo contains only the Client-LINK software (PC software).

## The purpose
The purpose of this project is to create a custom protocol to allow STM32-USB devices to exchange data with a PC, based on a single protocol that can be modified as needed.
LINK separates different projects by "Application", defined by an "APP-ID". Currently, the APP-ID of this example project is "DRAGON".
All commands require the APP-ID in the command, except "GETAPP", which returns the APP-ID (note: GETAPP isn't implemented in this example project. As mentioned earlier, the protocol can be adjusted).
Only one command is always implemented: "GETV". This command returns the main information of the device and is used by the Client to determine if the device is: a LINK device and from the correct application.

## How install Device-LINK in a project
As mentioned, the STM32 example code isn't included yet.
Once it is, the code can be implemented in any protocol you want. It was designed for USB, but you can use it with USART, CAN, Ethernet, or even I2C or SPI with some changes.
For example, if your project doesn't have a USB peripheral, you can use a UART with a USB-TTL converter (like the CH340G, even if it's not the best) and implement the LINK protocol on the UART side.

# LEGALS

## License

This project is licensed under the Apache License 2.0.  
You may obtain a copy of the license at [http://www.apache.org/licenses/LICENSE-2.0](http://www.apache.org/licenses/LICENSE-2.0).  

### Permissions
- Commercial use ✅  
- Modification ✅  
- Distribution ✅  
- Private use ✅  

### Conditions
- You must include the original copyright and license notice in any copies or substantial portions of the software.  
- If you modify the code, you must include a notice stating that you changed it.  
- No trademark use is granted.  

### Limitations
- The software is provided "as is", without warranty of any kind.  
- Liability is disclaimed for any damages resulting from the use of the software.
