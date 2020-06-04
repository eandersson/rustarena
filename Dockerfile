FROM didstopia/rust-server:latest

ADD rustarena.sh /app/rustarena.sh

ENV RUST_SERVER_STARTUP_ARGUMENTS "-batchmode -load -nographics +server.secure 1 +server.levelurl https://raw.githubusercontent.com/eandersson/rustarena/master/maps/rustarena.map"
ENV RUST_SERVER_IDENTITY "rustarena"
ENV RUST_SERVER_NAME "Rust Arena"
ENV RUST_SERVER_DESCRIPTION "Rust Arena"
ENV RUST_OXIDE_ENABLED "1"
ENV RUST_OXIDE_UPDATE_ON_BOOT "1"
ENV RUST_SERVER_MAXPLAYERS "32"
ENV RUST_SERVER_SAVE_INTERVAL "600000"

CMD [ "bash", "/app/rustarena.sh"]
