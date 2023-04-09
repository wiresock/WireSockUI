# WireSockUI

*WireSockUI is a complete rewrite of the EpexGUI initiative, credits to [EpexGUI](https://github.com/Epenko1337/EpexGUI) author for kicking this off*

WireSockUI provides an user-interface to use the [WireSock VPN client](https://www.wiresock.net) directly from your Windows system tray.
The interface design for WireSockUI follows the official [WireGuard for Windows[(https://www.wireguard.com/install/#windows-7-81-10-11-2008r2-2012r2-2016-2019-2022) interface.

### Main screen

![main-interface](https://user-images.githubusercontent.com/6480052/230771736-d467ea72-aa16-46bc-9cbd-8477dcf4c2bb.png)

The main screen shows the configured tunnel interface, peer information and tunnel state on the right-hand side.

### Edit screen

Through `add tunnel`, a new tunnel can be created from scratch or loaded from a file:

![new-profile](https://user-images.githubusercontent.com/6480052/230771804-db5494f1-198e-4238-900f-abb95f94bbac.png)

Existing tunnels can be edited by selecting the tunnel in the tunnel list and clicking `Edit`:

![edit-profile](https://user-images.githubusercontent.com/6480052/230771826-ae3cf5ee-f6d4-411c-a69c-6eb805def928.png)

WireSockUI offers syntax highlighting while creating new or editing tunnel profiles. It will validate both the format of the profile configuration in terms of sections and keys as well as the actual key values (Numbers, IP Addresses, CIDR masks).

### Process screen

You can use the `Process` button on the edit screen, to easily select a process name to insert into the profile for the WireSock AllowedApps or DisallowedApps keys.

![select-process](https://user-images.githubusercontent.com/6480052/230771894-b907c183-cdb2-48f2-8d58-03223b4c1ff8.png)

### Settings screen

Additionally WireSockUI supports a number of settings to allow it to start-up with Windows, automatically re-connect to your last active tunnel or minimize to system tray on startup. You can also open the folder where WireSockUI saves the profile configurations from here.

![settings](https://user-images.githubusercontent.com/6480052/230771932-11df9a15-df61-4657-bbf3-e8dbbdac6716.png)

------

New builds are automatically generated by GitHub Actions and made available as a release. The workflow logs can be viewed publicly, allowing potential applications to validate both the source code and the resulting artifact(s).
