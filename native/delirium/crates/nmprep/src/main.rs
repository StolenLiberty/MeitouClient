// nmprep: the GTA IV import (delirium_physics::prep) as a command. Usage: nmprep <GTA IV folder> <output folder>
fn main() {
    let a: Vec<String> = std::env::args().collect();
    if a.len() < 3 {
        eprintln!("usage: nmprep <GTA IV folder> <output folder>");
        std::process::exit(2);
    }
    match delirium_physics::prep::prepare(std::path::Path::new(&a[1]), std::path::Path::new(&a[2])) {
        Ok(made) => for (f, n, m) in made { println!("{f}: {n} parts, {m:.1} kg"); },
        Err(e) => { eprintln!("nmprep: {e}"); std::process::exit(1); }
    }
}
