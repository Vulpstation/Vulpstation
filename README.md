# Vulpstation
Vulpstation is a medium-roleplay 18+ fork of Floofstation.

We are a medium-roleplay 18+ furry-friendly Space Station 14 server, with focus on continuous roleplay, where players are expected to immerse into their characters and act in accordance to their stories and personalities rather than just their current job. The server permits adult roleplay so long as it is done in a more subtle manner, but does not consider it a sacred or protected thing.

[Floof Station](https://github.com/floof-station/floof-station) was a fork of [Einstein-Engines](https://github.com/Simple-Station/Einstein-Engines), and so is Vulpstation.


## Contributing
We are happy to accept contributions from anybody, come join our Discord if you want to help!

## Building

Refer to [the Space Wizards' guide](https://docs.spacestation14.com/en/general-development/setup/setting-up-a-development-environment.html) on setting up a development environment and for general information. But do keep in mind that Einstein Engines, the codebase Floof Station is based on, is an alternative codebase to the base one provided by WizDen, and many things may thus not apply nor be the same.
We provide some scripts shown below to make the job easier.

### Build dependencies

> - Git
> - .NET SDK 9.0.100


### Windows

> 1. Clone this repository
> 2. Run `git submodule update --init --recursive` in a terminal to download the engine
> 3. Run `Scripts/bat/buildAllDebug.bat` after making any changes to the source
> 4. Run `Scripts/bat/runQuickAll.bat` to launch the client and the server
> 5. Connect to localhost in the client and play

### Linux

> 1. Clone this repository
> 2. Run `git submodule update --init --recursive` in a terminal to download the engine
> 3. Run `Scripts/sh/buildAllDebug.sh` after making any changes to the source
> 4. Run `Scripts/sh/runQuickAll.sh` to launch the client and the server
> 5. Connect to localhost in the client and play

### MacOS

> I don't know anybody using MacOS to test this, but it's probably roughly the same steps as Linux

## License

Please read the [LEGAL.md](./LEGAL.md) file for information on the licenses of the code and assets in this repository.
