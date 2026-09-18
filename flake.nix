{
  # Exists for one reason: the Lean Kernel Arena builds every checker inside its own nix shell, and that
  # shell has elan, rustc, node, ocaml, zig, ghc and pypy, and nothing at all for .NET. Checkers that need
  # something else bring their own with `nix develop path:.`, which is what lean4cobol does. This is that.
  #
  # It is not how anyone is expected to build Tenet day to day; `dotnet build` with the .NET 10 SDK is.
  description = "Tenet, an independent implementation of the Lean 4 kernel on .NET";

  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";

  outputs = { self, nixpkgs }:
    let
      systems = [ "x86_64-linux" "aarch64-linux" "x86_64-darwin" "aarch64-darwin" ];
      forAll = f: nixpkgs.lib.genAttrs systems (system: f nixpkgs.legacyPackages.${system});
    in
    {
      devShells = forAll (pkgs: {
        default = pkgs.mkShell {
          # global.json pins the SDK to 10.0.1xx with rollForward latestFeature, so an older SDK is not
          # merely slower here, it refuses to start.
          packages = [ pkgs.dotnetCorePackages.sdk_10_0 ];

          DOTNET_CLI_TELEMETRY_OPTOUT = "1";
          DOTNET_NOLOGO = "1";
          # Under `--ignore-environment` there is no usable HOME, and the SDK wants somewhere to put its
          # first-run marker and the NuGet cache. Keep both inside the working tree.
          shellHook = ''
            export HOME="''${HOME:-$PWD/.nix-home}"
            export DOTNET_CLI_HOME="$PWD/.nix-home/dotnet"
            export NUGET_PACKAGES="$PWD/.nix-home/nuget"
            mkdir -p "$DOTNET_CLI_HOME" "$NUGET_PACKAGES"
          '';
        };
      });
    };
}
