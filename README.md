# Mandelbrot Viewer

This project is a graphical application for visualizing the Mandelbrot set. It demonstrates various rendering techniques, including sequential, parallel, and dataflow-based approaches, as part of the course "Parallel and Asynchronous Programming with .NET" at the JKU. The application allows users to zoom into the fractal, adjust rendering methods, and set the maximum number of iterations.

## Features

- **Interactive Mandelbrot Visualization**: Explore the Mandelbrot set with zooming and panning capabilities.
- **Multiple Rendering Methods**: Choose from Normal, Parallel, TPL (Task Parallel Library), and TDF (Task Dataflow) rendering methods to visualize the fractal.
- **Customizable Iterations**: Adjust the maximum number of iterations for more detailed or faster rendering.
- **Smooth Color Gradients**: Enjoy visually appealing fractals with smooth color transitions.
- **Real-Time Progress Updates**: View rendering progress in real-time with a progress bar.
- **Keyboard Shortcuts**: Use arrow keys for panning and 'R' to reset the view.
- **Mouse Interaction**: Drag to select a zoom area and refine your view.
- **Cancellation Support**: Cancel ongoing rendering tasks with a single click.

## Rendering Methods

The Mandelbrot Viewer supports multiple rendering methods, each demonstrating different programming paradigms for parallel and asynchronous computation. Below is a description of each method with code examples:

### Normal Rendering
This is the default sequential rendering method. It calculates each pixel one by one.

```csharp
private async void RenderNormal(int width, int height, byte[] pixels, int stride)
{
    for (int y = 0; y < height; y++)
    {
        for (int x = 0; x < width; x++)
        {
            if (_cancellationToken.IsCancellationRequested)
                return;
            calculateFractal(x, y, width, height, pixels, stride);
        }
        DrawLine(y, width, pixels, stride);
        UpdateProgress(y + 1, height);
    }
}
```

### Parallel Rendering
This method uses `Parallel.For` to calculate rows of pixels in parallel.

```csharp
private void RenderParallel(int width, int height, byte[] pixels, int stride)
{
    int progress = 0;
    Parallel.For(0, height, y =>
    {
        for (int x = 0; x < width; x++)
        {
            if (_cancellationToken.IsCancellationRequested)
                return;
            calculateFractal(x, y, width, height, pixels, stride);
        }
        DrawLine(y, width, pixels, stride);
        UpdateProgress(++progress, height);
    });
}
```

### TPL (Task Parallel Library) Rendering
This method uses tasks to process rows of pixels asynchronously.

```csharp
private void RenderTPL(int width, int height, byte[] pixels, int stride)
{
    int progress = 0;
    Task.WhenAll(
        Enumerable.Range(0, height).Select(y =>
        {
            return Task.Run(() =>
            {
                for (int x = 0; x < width; x++)
                {
                    if (_cancellationToken.IsCancellationRequested)
                        return;
                    calculateFractal(x, y, width, height, pixels, stride);
                }
                DrawLine(y, width, pixels, stride);
                UpdateProgress(++progress, height);
            });
        })
    ).Wait();
}
```

### TDF (Task Dataflow) Rendering
This method uses the Dataflow library to create a pipeline for rendering the fractal.

```csharp
private void RenderTDF(int width, int height, byte[] pixels, int stride)
{
    var options = new ExecutionDataflowBlockOptions
    {
        MaxDegreeOfParallelism = Environment.ProcessorCount,
        CancellationToken = _cancellationToken
    };

    //Generate Blocks
    var calculateIterationsBlock = new TransformBlock<(int x, int y), (int x, int y, double zx, double zy, int iterations)>(input =>
    {
        // calculate stuff
        return (x, y,zx,zy, iter);
    }, options);
    var colorizeBlock = new TransformBlock<(int x, int y, double zx, double zy ,int iterations), (int x, int y, Color)>(input =>
    {
        // generate color
        return (x, y, color);

    });
    var batchBlock = new BatchBlock<(int x, int y, Color)>(1000, new GroupingDataflowBlockOptions
    {
       // batch pixels
    });
    var renderBlock = new ActionBlock<(int x, int y, Color color)[]>(batch =>
    {
        // render batched pixels
    }

    // Wire the blocks
    calculateIterationsBlock.LinkTo(colorizeBlock, new DataflowLinkOptions { PropagateCompletion = true });
    colorizeBlock.LinkTo(batchBlock, new DataflowLinkOptions { PropagateCompletion = true });
    batchBlock.LinkTo(renderBlock, new DataflowLinkOptions { PropagateCompletion = true });

    // Post all pixels into the pipeline
    for (int y = 0; y < height; y++)
    {
        for (int x = 0; x < width; x++)
        {
            calculateIterationsBlock.SendAsync((x, y));                
        }
    }
}
```

Each method offers a unique way to render the Mandelbrot set, allowing users to explore the trade-offs between simplicity, performance, and scalability.

