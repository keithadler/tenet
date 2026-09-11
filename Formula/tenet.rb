# Homebrew formula for Tenet. Until a tap exists this can be installed directly:
#   brew install --build-from-source ./Formula/tenet.rb
# With a tap (keithadler/homebrew-tenet):
#   brew install keithadler/tenet/tenet
class Tenet < Formula
  desc "Independent implementation of the Lean 4 kernel, and a checker for Lean proofs"
  homepage "https://github.com/keithadler/tenet"
  license any_of: ["MIT", "Apache-2.0"]
  version "0.6.0"

  on_macos do
    on_arm do
      url "https://github.com/keithadler/tenet/releases/download/v0.6.0/tenet-v0.6.0-osx-arm64.tar.gz"
      # sha256 filled in at release time; `brew fetch` reports the value to paste here.
    end
  end

  on_linux do
    on_intel do
      url "https://github.com/keithadler/tenet/releases/download/v0.6.0/tenet-v0.6.0-linux-x64.tar.gz"
    end
  end

  def install
    bin.install "tenet"
    bash_completion.install "completions/tenet.bash" => "tenet" if File.exist?("completions/tenet.bash")
    zsh_completion.install "completions/_tenet" if File.exist?("completions/_tenet")
  end

  test do
    assert_match "tenet", shell_output("#{bin}/tenet version")
  end
end
