mod virtual_display;

use std::error::Error;
use std::io;
use std::time::Duration;

use virtual_display::VirtualDisplay;

fn main() -> Result<(), Box<dyn Error>> {
    let mut arguments = std::env::args().skip(1);
    if arguments.next().as_deref() != Some("create") {
        usage();
    }

    let hold = match arguments.next().as_deref() {
        None => None,
        Some("--hold-ms") => {
            let milliseconds = arguments
                .next()
                .ok_or("--hold-ms requires a value")?
                .parse::<u64>()?;
            Some(Duration::from_millis(milliseconds))
        }
        Some(_) => usage(),
    };
    if arguments.next().is_some() {
        usage();
    }

    let display = VirtualDisplay::create()?;
    println!("created={}", display.instance_id());

    if let Some(duration) = hold {
        std::thread::sleep(duration);
    } else {
        println!("Press Enter to remove the virtual display.");
        let mut line = String::new();
        io::stdin().read_line(&mut line)?;
    }

    drop(display);
    println!("removed");
    Ok(())
}

fn usage() -> ! {
    eprintln!("usage: sbms create [--hold-ms <milliseconds>]");
    std::process::exit(2);
}
